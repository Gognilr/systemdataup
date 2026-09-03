using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 「取消」要真的取消（C6 / C7）。
///
/// 取消一次传输原先只把会话状态改成 cancelled，随后的自动链路会把它原样撤销：
/// CommandService 判「这个候选没有 committed、也没有 InFlight 会话」→ 认定上传从没落地
/// → 指令复位重发；建会话时旧会话是 cancelled、幂等键被释放 → 建一个全新会话从 0 重传。
/// 于是「取消」的实际效果是「几分钟后从头再传一遍」，而使用者会认为这个按钮坏了。
///
/// 修法是把取消记在**候选**上——那是「这一份备份」的身份，会话只是它的一次尝试。
/// 这一组同时要钉住反面：标记不能是一道没有出口的闸，
/// 源文件变了或管理员显式再下发一次，都要能解除它。
/// </summary>
[Collection("postgres")]
public class UploadCancellationTests : IDisposable
{
    private const int ChunkSize = 16;

    private readonly PostgresDatabaseFixture _fixture;
    private readonly string _stagingRoot;

    public UploadCancellationTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        _stagingRoot = Path.Combine(Path.GetTempPath(), "bm-cancel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_stagingRoot, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    /// <summary>
    /// 核心回归：取消一次传输之后，下一轮预检不再自动下发这个候选的上传。
    /// </summary>
    [Fact]
    public async Task 取消传输后下一轮预检不再自动下发上传()
    {
        var dispatcher = new RecordingCommandDispatcher();
        await using var sp = BuildServices(dispatcher);
        var (clientId, taskId) = await SeedTaskAsync(sp);
        var files = MakeFiles(("db.bak", 40));

        var candidateId = await PrecheckAsync(sp, clientId, taskId, files);
        Assert.Equal(1, dispatcher.Dispatched.Count(t => t == CommandType.UploadCandidate));

        var sessionId = await CreateSessionAsync(sp, clientId, candidateId, files);

        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUploadSessionControlService>().CancelAsync(sessionId);
        }

        // 取消要落在候选上，不能只落在会话上
        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.NotNull((await db.CandidateBackupSets.AsNoTracking().SingleAsync(c => c.Id == candidateId)).CancelledAt);
        }

        // 源文件一个字节都没变，再扫一遍：不能再下发上传
        await PrecheckAsync(sp, clientId, taskId, files);
        Assert.Equal(1, dispatcher.Dispatched.Count(t => t == CommandType.UploadCandidate));
    }

    /// <summary>
    /// 另一条路：Agent 拿着一条复位重发的上传指令回来建会话。
    /// 这里不挡住的话，取消同样会变成「从 0 重传一遍」。
    /// </summary>
    [Fact]
    public async Task 取消之后不允许再为这个候选建会话()
    {
        var dispatcher = new RecordingCommandDispatcher();
        await using var sp = BuildServices(dispatcher);
        var (clientId, taskId) = await SeedTaskAsync(sp);
        var files = MakeFiles(("db.bak", 40));

        var candidateId = await PrecheckAsync(sp, clientId, taskId, files);
        var sessionId = await CreateSessionAsync(sp, clientId, candidateId, files);

        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUploadSessionControlService>().CancelAsync(sessionId);
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var uploads = scope.ServiceProvider.GetRequiredService<IUploadSessionService>();
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                uploads.CreateSessionAsync(clientId, new CreateUploadSessionRequest
                {
                    CandidateBackupSetId = candidateId,
                    TotalFiles = files.Count,
                    TotalBytes = files.Sum(f => f.SizeBytes),
                    ChunkSizeBytes = ChunkSize
                }));
            Assert.Equal(409, ex.StatusCode);
        }
    }

    /// <summary>
    /// 取消标记必须有出口。源文件变了就是一份新的备份，管理员取消的是刚才那一份，
    /// 不是「这个目录以后都别传」——不清掉标记的话，这台机器从此再也传不上东西。
    /// </summary>
    [Fact]
    public async Task 源文件变化后重新预检会解除取消标记()
    {
        var dispatcher = new RecordingCommandDispatcher();
        await using var sp = BuildServices(dispatcher);
        var (clientId, taskId) = await SeedTaskAsync(sp);

        var candidateId = await PrecheckAsync(sp, clientId, taskId, MakeFiles(("db.bak", 40)));
        var sessionId = await CreateSessionAsync(sp, clientId, candidateId, MakeFiles(("db.bak", 40)));

        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUploadSessionControlService>().CancelAsync(sessionId);
        }

        // 第二天的备份：同一个候选键，内容变了
        var dispatchedBefore = dispatcher.Dispatched.Count(t => t == CommandType.UploadCandidate);
        await PrecheckAsync(sp, clientId, taskId, MakeFiles(("db.bak", 64), ("db.properties", 12)));

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null((await db.CandidateBackupSets.AsNoTracking().SingleAsync(c => c.Id == candidateId)).CancelledAt);
        }

        Assert.Equal(dispatchedBefore + 1, dispatcher.Dispatched.Count(t => t == CommandType.UploadCandidate));
    }

    /// <summary>
    /// C7：「取消这次执行」要停住在跑的那几项，并给这一轮的候选打上取消标记——
    /// 否则在跑的预检继续扫，扫完照样自动下发全部单元的上传，
    /// 人点取消想要的是「别再传了」，拿到的却是「再等十分钟然后开始传」。
    /// </summary>
    [Fact]
    public async Task 取消整次执行会停掉在跑的项并给候选打标记()
    {
        var dispatcher = new RecordingCommandDispatcher();
        await using var sp = BuildServices(dispatcher);
        var (clientId, taskId) = await SeedTaskAsync(sp);
        var files = MakeFiles(("db.bak", 40));

        var candidateId = await PrecheckAsync(sp, clientId, taskId, files);
        var sessionId = await CreateSessionAsync(sp, clientId, candidateId, files);

        // 一次正在跑的执行：预检项已经下发、还在等结果
        Guid runId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var run = new Core.Entities.Execution.ExecutionRun
            {
                Id = Guid.NewGuid(),
                Kind = ExecutionRunKind.Manual,
                Name = "取消测试",
                Status = BatchStatus.Running,
                MaxConcurrent = 1,
                ItemTimeoutMinutes = 120,
                TriggerSource = "test",
                CreatedAt = now.AddMinutes(-5),
                StartedAt = now.AddMinutes(-5),
                TotalItems = 1
            };
            run.Items.Add(new Core.Entities.Execution.ExecutionRunItem
            {
                Id = Guid.NewGuid(),
                SortOrder = 0,
                ClientId = clientId,
                TaskId = taskId,
                CommandType = CommandType.PrecheckTask,
                Status = ExecutionItemStatus.Running,
                StartedAt = now.AddMinutes(-5)
            });
            db.ExecutionRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IExecutionQueueService>().CancelRunAsync(runId);
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var item = await db.ExecutionRunItems.AsNoTracking().SingleAsync(i => i.RunId == runId);
            Assert.Equal(ExecutionItemStatus.Cancelled, item.Status);

            var session = await db.UploadSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
            Assert.Equal(UploadStatus.Cancelled, session.Status);

            var candidate = await db.CandidateBackupSets.AsNoTracking().SingleAsync(c => c.Id == candidateId);
            Assert.NotNull(candidate.CancelledAt);
        }
    }

    // ---------- 步骤 ----------

    private static async Task<Guid> PrecheckAsync(
        ServiceProvider sp, Guid clientId, Guid taskId, List<PrecheckFileDto> files)
    {
        await using var scope = sp.CreateAsyncScope();
        var precheck = scope.ServiceProvider.GetRequiredService<IAgentPrecheckService>();
        var response = await precheck.SubmitResultAsync(clientId, taskId, PassedRequest(files));
        Assert.True(response.Accepted);
        return response.CandidateBackupSetId!.Value;
    }

    private static async Task<Guid> CreateSessionAsync(
        ServiceProvider sp, Guid clientId, Guid candidateId, List<PrecheckFileDto> files)
    {
        await using var scope = sp.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<IUploadSessionService>();
        var created = await uploads.CreateSessionAsync(clientId, new CreateUploadSessionRequest
        {
            CandidateBackupSetId = candidateId,
            TotalFiles = files.Count,
            TotalBytes = files.Sum(f => f.SizeBytes),
            ChunkSizeBytes = ChunkSize
        });
        return created.UploadSessionId;
    }

    // ---------- 基础设施 ----------

    private ServiceProvider BuildServices(RecordingCommandDispatcher dispatcher)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;
        using (var db = new AppDbContext(options))
        {
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'staging_path'",
                JsonSerializer.Serialize(_stagingRoot));
            // 全局上传名额是全库共享的配置，并发那一组会把它压到 1 或 2 并留下活动会话。
            // 名额一满，自动下发就改成排队，这一组要断言的「下发/不下发」就变成了噪声。
            db.Database.ExecuteSqlRaw(
                "INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by) "
                + "VALUES ({0}, '64'::jsonb, false, NULL) "
                + "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value",
                SequentialExecutionWorker.GlobalUploadLimitKey);
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
        sc.AddScoped<IUploadSessionControlService, UploadSessionControlService>();
        sc.AddScoped<IAlertingService, NoopAlertingService>();
        sc.AddSingleton<ICommandDispatcher>(dispatcher);
        sc.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        sc.AddScoped<IAgentPrecheckService, AgentPrecheckService>();
        var provider = sc.BuildServiceProvider();
        provider.GetRequiredService<SystemSettingsProvider>().Invalidate();
        return provider;
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
            MachineId = $"cx-{suffix}",
            Hostname = $"CX-{suffix[..8]}",
            DisplayName = $"取消回归客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"cx-task-{suffix[..8]}",
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

    /// <summary>固定时间戳：清单哈希要可重现，「源文件没变」这个前提才成立。</summary>
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
        CandidateKey = "cx-candidate",   // 同一个候选键 = 同一份备份被反复扫到
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
