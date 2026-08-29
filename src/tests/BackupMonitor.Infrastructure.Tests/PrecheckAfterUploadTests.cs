using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 「传过一次就再也预检不了」回归测试。
///
/// 现场表现：任务第一次预检通过、自动下发上传、会话建起来——哪怕随即被取消，
/// 之后每一次预检（cron 到点自动扫、人点「下发预检」都一样）都返回 500
/// 「服务器内部错误，请稍后重试」，告警中心堆满 precheck_task 指令执行失败。
/// 起因是 upload_files.candidate_file_id 以 NOT NULL + NO ACTION 引用候选清单，
/// 而预检通过时要整体替换清单，DELETE 撞外键 23503（V026 把引用改成可空 + SET NULL）。
///
/// 这条路径值得一条真库测试：外键行为在内存库里根本不存在，
/// 而它一旦回归，症状是整个产品最核心的那件事——按时备份——静默停摆。
/// </summary>
[Collection("postgres")]
public class PrecheckAfterUploadTests : IDisposable
{
    private const int ChunkSize = 16;

    private readonly PostgresDatabaseFixture _fixture;
    private readonly string _stagingRoot;

    public PrecheckAfterUploadTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        _stagingRoot = Path.Combine(Path.GetTempPath(), "bm-precheck-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_stagingRoot, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    [Fact]
    public async Task 上传会话建过之后仍能再次预检()
    {
        await using var sp = BuildServices();
        var (clientId, taskId) = await SeedTaskAsync(sp);

        // 第一次预检：通过 → 落候选与清单
        var first = MakeFiles(("db.bak", 40), ("db.properties", 12));
        Guid candidateId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var precheck = scope.ServiceProvider.GetRequiredService<IAgentPrecheckService>();
            var response = await precheck.SubmitResultAsync(clientId, taskId, PassedRequest(first));
            Assert.True(response.Accepted);
            candidateId = response.CandidateBackupSetId!.Value;
        }

        // 上传会话：upload_files 就是在这里引用上候选清单的
        Guid sessionId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var uploads = scope.ServiceProvider.GetRequiredService<IUploadSessionService>();
            var created = await uploads.CreateSessionAsync(clientId, new CreateUploadSessionRequest
            {
                CandidateBackupSetId = candidateId,
                TotalFiles = first.Count,
                TotalBytes = first.Sum(f => f.SizeBytes),
                ChunkSizeBytes = ChunkSize
            });
            sessionId = created.UploadSessionId;
            Assert.Equal(first.Count, created.Files.Count);

            // 一个字节都没传就取消——这正是现场那次（12:04:50 建会话、12:04:51 取消）
            await uploads.CancelSessionAsync(clientId, sessionId);
        }

        // 第二次预检：同一个候选键、不同清单 → 必须能整体替换，而不是 23503
        var second = MakeFiles(("db.bak", 64), ("db.properties", 12), ("extra.zip", 8));
        await using (var scope = sp.CreateAsyncScope())
        {
            var precheck = scope.ServiceProvider.GetRequiredService<IAgentPrecheckService>();
            var response = await precheck.SubmitResultAsync(clientId, taskId, PassedRequest(second));
            Assert.True(response.Accepted);
            Assert.Equal(candidateId, response.CandidateBackupSetId);
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var paths = await db.Set<CandidateFile>().AsNoTracking()
                .Where(f => f.CandidateBackupSetId == candidateId)
                .OrderBy(f => f.RelativePath)
                .Select(f => f.RelativePath)
                .ToListAsync();
            Assert.Equal(new[] { "db.bak", "db.properties", "extra.zip" }, paths);

            // 旧会话的行留着（它自带 relative_path/size/sha256），只是不再指向已被替换的清单
            var orphaned = await db.UploadFiles.AsNoTracking()
                .Where(f => f.UploadSessionId == sessionId)
                .ToListAsync();
            Assert.Equal(2, orphaned.Count);
            Assert.All(orphaned, f => Assert.Null(f.CandidateFileId));
        }
    }

    [Fact]
    public async Task 清单没变时不重建候选文件行()
    {
        await using var sp = BuildServices();
        var (clientId, taskId) = await SeedTaskAsync(sp);
        var files = MakeFiles(("db.bak", 40));

        Guid candidateId;
        List<Guid> firstIds;
        await using (var scope = sp.CreateAsyncScope())
        {
            var precheck = scope.ServiceProvider.GetRequiredService<IAgentPrecheckService>();
            candidateId = (await precheck.SubmitResultAsync(clientId, taskId, PassedRequest(files)))
                .CandidateBackupSetId!.Value;
            firstIds = await CandidateFileIdsAsync(scope, candidateId);
        }

        // 同一份备份被再扫一次（cron 一天扫一遍就是这个场景）：清单逐字节相同，
        // 不该把几千行删掉再原样插回去。
        await using (var scope = sp.CreateAsyncScope())
        {
            var precheck = scope.ServiceProvider.GetRequiredService<IAgentPrecheckService>();
            await precheck.SubmitResultAsync(clientId, taskId, PassedRequest(files));
            Assert.Equal(firstIds, await CandidateFileIdsAsync(scope, candidateId));
        }
    }

    // ---------- 基础设施 ----------

    private static async Task<List<Guid>> CandidateFileIdsAsync(AsyncServiceScope scope, Guid candidateId)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Set<CandidateFile>().AsNoTracking()
            .Where(f => f.CandidateBackupSetId == candidateId)
            .OrderBy(f => f.SortOrder)
            .Select(f => f.Id)
            .ToListAsync();
    }

    private ServiceProvider BuildServices()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;
        using (var db = new AppDbContext(options))
        {
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'staging_path'",
                JsonSerializer.Serialize(_stagingRoot));
        }

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddScoped<IUploadStorage, UploadStorage>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddKeyedSingleton(QueueKeys.Commit, (_, _) => Channel.CreateUnbounded<WorkItem>());
        sc.AddScoped<IUploadSessionService, UploadSessionService>();
        sc.AddScoped<IAlertingService, NoopAlertingService>();
        sc.AddScoped<ICommandDispatcher, RecordingCommandDispatcher>();
        sc.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        sc.AddScoped<IAgentPrecheckService, AgentPrecheckService>();
        return sc.BuildServiceProvider();
    }

    private static async Task<(Guid ClientId, Guid TaskId)> SeedTaskAsync(ServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"pau-{suffix}",
            Hostname = $"PAU-{suffix[..8]}",
            DisplayName = $"预检回归客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"pau-task-{suffix[..8]}",
            ApplicationName = "OA",
            SourcePath = @"D:\Seeyon\A6\Backup\*\*",
            RecognizerType = RecognizerType.MultiFileSet,
            RecognizerConfig = "{}",
            TaskMode = TaskMode.Automatic,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return (client.Id, task.Id);
    }

    /// <summary>固定时间戳：清单哈希要可重现，不能跟着 DateTime.UtcNow 漂。</summary>
    private static readonly DateTime FileTime = new(2026, 8, 27, 3, 0, 0, DateTimeKind.Utc);

    private static List<PrecheckFileDto> MakeFiles(params (string Path, int Size)[] files) =>
        files.Select(f => new PrecheckFileDto
        {
            RelativePath = f.Path,
            SizeBytes = f.Size,
            LastModifiedAt = FileTime,
            Sha256 = Hex(SHA256.HashData(Encoding.UTF8.GetBytes($"{f.Path}:{f.Size}")))
        }).ToList();

    private static SubmitPrecheckResultRequest PassedRequest(List<PrecheckFileDto> files) => new()
    {
        CandidateKey = "pau-candidate",   // 同一个候选键 = 同一份备份被反复扫到
        SourceRoot = @"D:\Seeyon\A6\Backup\2026",
        PrecheckStatus = "passed",
        TotalFiles = files.Count,
        TotalBytes = files.Sum(f => f.SizeBytes),
        ManifestHash = ManifestHash(files),
        Files = files
    };

    /// <summary>与 AgentPrecheckService.ValidatePassedResult 的算法逐字对齐。</summary>
    private static string ManifestHash(List<PrecheckFileDto> files)
    {
        var manifest = string.Join('\n', files
            .Select(f => new
            {
                RelativePath = f.RelativePath.Replace('\\', '/'),
                f.SizeBytes,
                f.LastModifiedAt,
                Sha256 = f.Sha256!.ToLowerInvariant()
            })
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{f.RelativePath}|{f.SizeBytes}|{f.LastModifiedAt.Ticks}|{f.Sha256}"));
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>记录下发过的指令；预检通过后的自动上传指令走这里，不需要真实签名链。</summary>
internal sealed class RecordingCommandDispatcher : ICommandDispatcher
{
    public List<CommandType> Dispatched { get; } = [];

    public Task<Command> CreateCommandAsync(
        Guid clientId,
        CommandType commandType,
        Guid? taskId = null,
        Guid? candidateBackupSetId = null,
        object? payload = null,
        int priority = 100,
        TimeSpan? ttl = null,
        string? idempotencyKey = null,
        Guid? createdBy = null,
        bool restartIfNotActive = false,
        CancellationToken ct = default)
    {
        Dispatched.Add(commandType);
        return Task.FromResult(new Command
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateBackupSetId = candidateBackupSetId,
            CommandType = commandType,
            Status = CommandStatus.Pending
        });
    }
}
