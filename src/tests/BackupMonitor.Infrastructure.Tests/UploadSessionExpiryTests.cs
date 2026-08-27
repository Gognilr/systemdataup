using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Upload;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 上传会话生命周期回收集成测试（审计 A-03 / A-04）。
///
/// 这三件事此前全系统无人执行：会话超时、终态会话暂存清理、孤儿目录清理。
/// 后果是一条完整的故障链——僵尸会话攒够 max_concurrent_uploads 之后
/// 该客户端永久 409，同时 .part 文件填满暂存盘让全体客户端 507。
/// 用例直接驱动 LifecycleExpiryWorker.RunPassAsync，不赌 BackgroundService 的时序。
/// </summary>
[Collection("postgres")]
public class UploadSessionExpiryTests : IDisposable
{
    private readonly PostgresDatabaseFixture _fixture;
    private readonly string _stagingRoot;

    public UploadSessionExpiryTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        _stagingRoot = Path.Combine(Path.GetTempPath(), "bm-expiry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_stagingRoot, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    // ---------- 用例 ----------

    [Fact]
    public async Task 过期判定优先用最后活动时间()
    {
        await using var sp = BuildServices();

        // 活动时间很旧但创建时间很新 → 仍然过期（不能因为「刚建的」就放过）
        var stale = await SeedSessionAsync(sp, UploadStatus.Uploading,
            createdAt: DateTime.UtcNow, lastActivityAt: DateTime.UtcNow.AddHours(-5));

        // 活动时间很新但创建时间很旧 → 正在正常传输，不能碰
        var alive = await SeedSessionAsync(sp, UploadStatus.Uploading,
            createdAt: DateTime.UtcNow.AddHours(-5), lastActivityAt: DateTime.UtcNow);

        await RunPassAsync(sp);

        Assert.Equal(UploadStatus.Expired, await StatusOfAsync(sp, stale));
        Assert.Equal(UploadStatus.Uploading, await StatusOfAsync(sp, alive));

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expired = await db.UploadSessions.AsNoTracking().FirstAsync(s => s.Id == stale);
        Assert.Equal("SESSION_TIMEOUT", expired.ErrorCode);
        Assert.NotNull(expired.CompletedAt);
    }

    [Fact]
    public async Task 最后活动时间为空时回落创建时间()
    {
        await using var sp = BuildServices();

        // 建了会话一个块都没传：LastActivityAt 为空，只能看 CreatedAt
        var neverActive = await SeedSessionAsync(sp, UploadStatus.Created,
            createdAt: DateTime.UtcNow.AddHours(-5), lastActivityAt: null);
        var fresh = await SeedSessionAsync(sp, UploadStatus.Created,
            createdAt: DateTime.UtcNow, lastActivityAt: null);

        await RunPassAsync(sp);

        Assert.Equal(UploadStatus.Expired, await StatusOfAsync(sp, neverActive));
        Assert.Equal(UploadStatus.Created, await StatusOfAsync(sp, fresh));
    }

    [Fact]
    public async Task 终态会话暂存目录被清理且不重复处理()
    {
        await using var sp = BuildServices();

        // Committed 也要兜底：入库成功时清过一次，但那次是 try/catch 吞异常的
        var committed = await SeedSessionAsync(sp, UploadStatus.Committed,
            createdAt: DateTime.UtcNow.AddDays(-3), lastActivityAt: DateTime.UtcNow.AddDays(-3),
            completedAt: DateTime.UtcNow.AddDays(-3), withStagingDirectory: true);
        var cancelled = await SeedSessionAsync(sp, UploadStatus.Cancelled,
            createdAt: DateTime.UtcNow.AddDays(-3), lastActivityAt: DateTime.UtcNow.AddDays(-3),
            completedAt: DateTime.UtcNow.AddDays(-3), withStagingDirectory: true);

        // 还在保留期内的终态会话：现场要留给排障，这一轮不能动
        var recent = await SeedSessionAsync(sp, UploadStatus.Failed,
            createdAt: DateTime.UtcNow, lastActivityAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow, withStagingDirectory: true);

        await RunPassAsync(sp);

        Assert.False(Directory.Exists(SessionDirectory(committed)));
        Assert.False(Directory.Exists(SessionDirectory(cancelled)));
        Assert.True(Directory.Exists(SessionDirectory(recent)));

        // StagingPath 置 null 即「已清过」，这是幂等标记：再跑一轮不会重复进候选集
        Assert.Null(await StagingPathOfAsync(sp, committed));
        Assert.Null(await StagingPathOfAsync(sp, cancelled));
        Assert.NotNull(await StagingPathOfAsync(sp, recent));

        // 重建目录后再跑一轮：已经置 null 的会话不该再被选中，目录应当还在
        Directory.CreateDirectory(SessionDirectory(committed));
        await RunPassAsync(sp);
        Assert.True(Directory.Exists(SessionDirectory(committed)));
    }

    [Fact]
    public async Task 孤儿目录被清理而不认识的目录不被碰()
    {
        await using var sp = BuildServices();

        var sessionsRoot = Path.Combine(_stagingRoot, "sessions");
        Directory.CreateDirectory(sessionsRoot);

        // 进程崩在「建目录」与「写库」之间留下的残骸：库里没有这一行
        var orphan = Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-3));

        // 刚建出来还没写库的目录：不能误删，否则会杀掉正在创建中的会话
        var young = Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(young);

        // 暂存根有可能被管理员指到还放着别的东西的盘，名字不认识的一律不碰
        var foreign = Path.Combine(sessionsRoot, "not-a-guid");
        Directory.CreateDirectory(foreign);
        Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-3));

        await RunPassAsync(sp);

        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(young));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public async Task 并发闸门在巡检之后重新放行()
    {
        await using var sp = BuildServices();

        // max_concurrent_uploads 默认 2：两个僵尸会话就足以永久锁死这台客户端
        var clientId = await SeedClientAsync(sp);
        await SeedSessionAsync(sp, UploadStatus.Uploading,
            createdAt: DateTime.UtcNow.AddHours(-5), lastActivityAt: DateTime.UtcNow.AddHours(-5), clientId: clientId);
        await SeedSessionAsync(sp, UploadStatus.Uploading,
            createdAt: DateTime.UtcNow.AddHours(-5), lastActivityAt: DateTime.UtcNow.AddHours(-5), clientId: clientId);

        Assert.Equal(2, await WritableCountAsync(sp, clientId));
        await RunPassAsync(sp);
        Assert.Equal(0, await WritableCountAsync(sp, clientId));
    }

    // ---------- 基础设施 ----------

    /// <summary>组装与生产一致的 DI（真实暂存目录通过 system_settings.staging_path 注入）</summary>
    private ServiceProvider BuildServices()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;
        using (var db = new AppDbContext(options))
        {
            var json = JsonSerializer.Serialize(_stagingRoot);
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'staging_path'",
                json);
            // 保留期下限是 1 小时，因此用例里把「已到期」的会话时间点造在几天前，
            // 而不是把保留期调到零——后者会连正在创建中的目录一起卷进去。
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = '3600'::jsonb WHERE setting_key = 'upload_session_timeout_seconds'");
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
        return sc.BuildServiceProvider();
    }

    private static async Task RunPassAsync(ServiceProvider sp)
    {
        var worker = new LifecycleExpiryWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<LifecycleExpiryWorker>>());
        using var scope = sp.CreateScope();
        await worker.RunPassAsync(scope, CancellationToken.None);
    }

    private string SessionDirectory(Guid sessionId)
        => Path.Combine(_stagingRoot, "sessions", sessionId.ToString("N"));

    private static async Task<UploadStatus> StatusOfAsync(ServiceProvider sp, Guid sessionId)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.UploadSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId)).Status;
    }

    private static async Task<string?> StagingPathOfAsync(ServiceProvider sp, Guid sessionId)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.UploadSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId)).StagingPath;
    }

    private static async Task<int> WritableCountAsync(ServiceProvider sp, Guid clientId)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.UploadSessions.AsNoTracking().CountAsync(s =>
            s.ClientId == clientId
            && (s.Status == UploadStatus.Created || s.Status == UploadStatus.Uploading
                || s.Status == UploadStatus.Paused || s.Status == UploadStatus.RetryWait));
    }

    private static async Task<Guid> SeedClientAsync(ServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (clientId, _) = await SeedClientAndCandidateAsync(db);
        return clientId;
    }

    /// <summary>造一个指定状态与时间点的会话；withStagingDirectory 时同时在磁盘上建出暂存目录</summary>
    private async Task<Guid> SeedSessionAsync(
        ServiceProvider sp,
        UploadStatus status,
        DateTime createdAt,
        DateTime? lastActivityAt,
        DateTime? completedAt = null,
        bool withStagingDirectory = false,
        Guid? clientId = null)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid ownerId;
        Guid taskId;
        Guid candidateId;
        if (clientId is null)
        {
            (ownerId, taskId, candidateId) = await SeedClientTaskCandidateAsync(db);
        }
        else
        {
            ownerId = clientId.Value;
            var candidate = await db.CandidateBackupSets.AsNoTracking().FirstAsync(c => c.ClientId == ownerId);
            taskId = candidate.TaskId;
            candidateId = candidate.Id;
        }

        var sessionId = Guid.NewGuid();
        var directory = SessionDirectory(sessionId);
        if (withStagingDirectory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "dummy.part"), "x");
        }

        db.UploadSessions.Add(new UploadSession
        {
            Id = sessionId,
            ClientId = ownerId,
            TaskId = taskId,
            CandidateBackupSetId = candidateId,
            Status = status,
            TotalFiles = 1,
            TotalBytes = 1,
            ChunkSizeBytes = 16,
            StagingPath = directory,
            StartedAt = createdAt,
            LastActivityAt = lastActivityAt,
            CompletedAt = completedAt,
            CreatedAt = createdAt,
            UpdatedAt = completedAt ?? createdAt
        });
        await db.SaveChangesAsync();
        return sessionId;
    }

    private static async Task<(Guid ClientId, Guid CandidateId)> SeedClientAndCandidateAsync(AppDbContext db)
    {
        var (clientId, _, candidateId) = await SeedClientTaskCandidateAsync(db);
        return (clientId, candidateId);
    }

    private static async Task<(Guid ClientId, Guid TaskId, Guid CandidateId)> SeedClientTaskCandidateAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"itexp-{suffix}",
            Hostname = $"itexp-host-{suffix[..8]}",
            DisplayName = $"过期测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"itexp-task-{suffix[..8]}",
            ApplicationName = "ExpiryTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            CreatedAt = now,
            UpdatedAt = now
        };
        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            TaskId = task.Id,
            CandidateKey = $"itexp-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        db.CandidateBackupSets.Add(candidate);
        await db.SaveChangesAsync();
        return (client.Id, task.Id, candidate.Id);
    }
}
