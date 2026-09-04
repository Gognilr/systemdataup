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
/// 「删了就再也备不回来」回归测试（C10）。
///
/// 删除备份集是软删（回收站 → recycle_bin，彻底删除 → deleted），两种删法行都还在。
/// 四道应用层闸（预检、建会话、指令复位、手动/批量上传）加一条数据库无条件唯一约束
/// 全都不看状态，于是源文件没变的那份备份，删掉之后永久无法重新入库——
/// 而使用者在界面上看到的只是「没有新备份」。
///
/// 这条链上任何一环回归，症状都相同且完全静默，所以整条链一起测：
/// 预检 → 建会话 → 入库 → 删除 → 源文件不变再预检 → 再建会话 → 再入库。
/// </summary>
[Collection("postgres")]
public class ReuploadAfterDeleteTests : IDisposable
{
    private const int ChunkSize = 16;

    private readonly PostgresDatabaseFixture _fixture;

    /// <summary>
    /// 同一个候选键 = 同一份备份被反复扫到；Agent 也拿它当建会话的幂等键。
    ///
    /// 每个测试实例各取一个，不能写成常量：upload_sessions 的幂等键是**全库唯一**的
    /// （uq_upload_sessions_idempotency，不按客户端分），同一个 fixture 里的用例
    /// 用同一个键就会互相撞成 23505，而那与被测的缺陷毫无关系。
    /// </summary>
    private readonly string _candidateKey = $"ru-candidate-{Guid.NewGuid():N}";

    private readonly string _stagingRoot;
    private readonly string _repositoryRoot;

    public ReuploadAfterDeleteTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        var root = Path.Combine(Path.GetTempPath(), "bm-reupload-tests", Guid.NewGuid().ToString("N"));
        _stagingRoot = Path.Combine(root, "staging");
        _repositoryRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(_stagingRoot);
        Directory.CreateDirectory(_repositoryRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_stagingRoot)!, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    /// <summary>
    /// 核心回归：备份 → 删除（回收站）→ 源文件一个字节都没变 → 能重新备份并重新入库。
    /// </summary>
    [Fact]
    public async Task 删除之后源文件不变仍能重新预检并重新入库()
    {
        await using var sp = BuildServices();
        var (clientId, taskId) = await SeedTaskAsync(sp);
        var files = MakeFiles(("db.bak", 40), ("db.properties", 12));

        // 第一次：预检 → 建会话 → 入库
        var candidateId = await PrecheckAsync(sp, clientId, taskId, files);
        var firstSessionId = await CreateSessionAsync(sp, clientId, candidateId, files);
        var firstSetId = await ArchiveAsync(sp, clientId, taskId, candidateId, firstSessionId);

        // 删进回收站
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupSetService>().RecycleAsync(firstSetId);
        }

        // 第二次：清单逐字节相同（源文件没变），必须一路走通到重新入库。
        // 修复前这里会在预检那一步就抛 CANDIDATE_ALREADY_ARCHIVED。
        var secondCandidateId = await PrecheckAsync(sp, clientId, taskId, files);
        Assert.Equal(candidateId, secondCandidateId);

        var secondSessionId = await CreateSessionAsync(sp, clientId, candidateId, files);
        Assert.NotEqual(firstSessionId, secondSessionId);

        // 数据库那条唯一索引也必须放行：同一个候选此刻只有一个「活着」的版本。
        var secondSetId = await ArchiveAsync(sp, clientId, taskId, candidateId, secondSessionId);

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(BackupSetStatus.RecycleBin,
                (await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == firstSetId)).Status);
            Assert.Equal(BackupSetStatus.Available,
                (await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == secondSetId)).Status);
        }
    }

    /// <summary>
    /// 删除要让 Agent 的两段式扫描基线失效。
    /// 不清的话上面那条链全都放行了也没用：源文件没变 → 快速指纹命中上一次的候选
    /// → Agent 直接回 no_new_backup，界面上显示「没有新备份」，
    /// 而使用者刚刚删掉的正是那一份。
    /// </summary>
    [Fact]
    public async Task 删除备份集会清掉快速指纹基线并推高配置版本()
    {
        await using var sp = BuildServices();
        var (clientId, taskId) = await SeedTaskAsync(sp);
        var files = MakeFiles(("db.bak", 40));

        var candidateId = await PrecheckAsync(sp, clientId, taskId, files);
        var sessionId = await CreateSessionAsync(sp, clientId, candidateId, files);
        var setId = await ArchiveAsync(sp, clientId, taskId, candidateId, sessionId);

        long versionBefore;
        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidate = await db.CandidateBackupSets.SingleAsync(c => c.Id == candidateId);
            candidate.QuickFingerprint = "quick-fingerprint-baseline";
            await db.SaveChangesAsync();
            versionBefore = (await db.BackupTasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).ConfigVersion;
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupSetService>().RecycleAsync(setId);
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null((await db.CandidateBackupSets.AsNoTracking().SingleAsync(c => c.Id == candidateId)).QuickFingerprint);
            Assert.True((await db.BackupTasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).ConfigVersion > versionBefore);
        }
    }

    /// <summary>
    /// 还原一份备份时，同一候选可能已经有新版本了。
    /// 不挡住的话，使用者拿到的是一句数据库唯一约束错误，看不出发生了什么。
    /// </summary>
    [Fact]
    public async Task 已经重新备份过之后不允许再还原旧版本()
    {
        await using var sp = BuildServices();
        var (clientId, taskId) = await SeedTaskAsync(sp);
        var files = MakeFiles(("db.bak", 40));

        var candidateId = await PrecheckAsync(sp, clientId, taskId, files);
        var firstSetId = await ArchiveAsync(sp, clientId, taskId, candidateId,
            await CreateSessionAsync(sp, clientId, candidateId, files));

        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupSetService>().RecycleAsync(firstSetId);
        }

        await ArchiveAsync(sp, clientId, taskId, candidateId,
            await CreateSessionAsync(sp, clientId, candidateId, files));

        await using (var scope = sp.CreateAsyncScope())
        {
            var sets = scope.ServiceProvider.GetRequiredService<IBackupSetService>();
            var ex = await Assert.ThrowsAsync<BusinessException>(() => sets.RestoreFromRecycleBinAsync(firstSetId));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("重新备份", ex.Message);
        }
    }

    // ---------- 链条的三步 ----------

    private async Task<Guid> PrecheckAsync(
        ServiceProvider sp, Guid clientId, Guid taskId, List<PrecheckFileDto> files)
    {
        await using var scope = sp.CreateAsyncScope();
        var precheck = scope.ServiceProvider.GetRequiredService<IAgentPrecheckService>();
        var response = await precheck.SubmitResultAsync(clientId, taskId, PassedRequest(files));
        Assert.True(response.Accepted);
        return response.CandidateBackupSetId!.Value;
    }

    /// <summary>
    /// 幂等键必须带上，而且两次用的是同一个——真实 Agent 就是这么发的
    /// （AgentWorker.CreateUploadSessionAsync：IdempotencyKey = candidate.CandidateKey，
    /// 候选键由源文件内容决定，源文件不变它就不变）。
    ///
    /// 这一条原先不带键，于是这整条「删了就再也备不回来」的链漏掉了第五道闸：
    /// 幂等分支只看会话状态，把那个 committed 的空壳原样交回，Agent 一个字节都不传
    /// 就回 UPLOAD_ACCEPTED。现场（2026-09-04 删光备份集后重跑计划）三个任务
    /// 全部显示「备份已入库」，而仓库目录里什么都没有——测试全绿，故障照发。
    /// </summary>
    private async Task<Guid> CreateSessionAsync(
        ServiceProvider sp, Guid clientId, Guid candidateId, List<PrecheckFileDto> files)
    {
        await using var scope = sp.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<IUploadSessionService>();
        var created = await uploads.CreateSessionAsync(clientId, new CreateUploadSessionRequest
        {
            CandidateBackupSetId = candidateId,
            TotalFiles = files.Count,
            TotalBytes = files.Sum(f => f.SizeBytes),
            ChunkSizeBytes = ChunkSize,
            IdempotencyKey = _candidateKey
        });
        return created.UploadSessionId;
    }

    /// <summary>
    /// 模拟「入库」：把会话置为 committed 并落一条正式备份集。
    /// 真实链路要经过分块上传与后台校验，那两段不是这条缺陷的被测对象；
    /// 这里要证明的是入库这一步会不会被状态过滤和唯一索引挡住。
    /// </summary>
    private async Task<Guid> ArchiveAsync(
        ServiceProvider sp, Guid clientId, Guid taskId, Guid candidateId, Guid sessionId)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionId);
        session.Status = UploadStatus.Committed;
        session.CompletedAt = now;
        session.CommittedAt = now;

        var repositoryPath = Path.Combine(_repositoryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryPath);

        var set = new BackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            SourceCandidateId = candidateId,
            UploadSessionId = sessionId,
            BackupSetCode = $"BS-RU-{Guid.NewGuid():N}"[..20],
            Status = BackupSetStatus.Available,
            DiscoveredAt = now,
            UploadedAt = now,
            VerifiedAt = now,
            RepositoryPath = repositoryPath,
            TotalFiles = 1,
            TotalBytes = 16,
            CreatedAt = now
        };
        db.BackupSets.Add(set);
        await db.SaveChangesAsync();
        return set.Id;
    }

    // ---------- 基础设施 ----------

    private ServiceProvider BuildServices()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;
        using (var db = new AppDbContext(options))
        {
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'staging_path'",
                JsonSerializer.Serialize(_stagingRoot));
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'repository_path'",
                JsonSerializer.Serialize(_repositoryRoot));
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
        sc.AddKeyedSingleton(QueueKeys.Verify, (_, _) => Channel.CreateUnbounded<WorkItem>());
        sc.AddScoped<IUploadSessionService, UploadSessionService>();
        sc.AddScoped<IBackupSetService, BackupSetService>();
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
            MachineId = $"ru-{suffix}",
            Hostname = $"RU-{suffix[..8]}",
            DisplayName = $"重传回归客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"ru-task-{suffix[..8]}",
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


    private SubmitPrecheckResultRequest PassedRequest(List<PrecheckFileDto> files) => new()
    {
        CandidateKey = _candidateKey,
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
