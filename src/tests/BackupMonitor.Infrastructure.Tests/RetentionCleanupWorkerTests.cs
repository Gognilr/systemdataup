using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Retention;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 保留策略清理工作器集成测试（DEV-PROMPTS 提示词 3b）：
/// 保留期边界（全部使用 UTC 时间，与 worker 内部 DateTime.UtcNow 对齐）、
/// retention_locks / locked 字段必须阻止删除、最后一个可用副本不得删除、回收站到期物理清除。
/// 通过真实 BackgroundService（StartAsync 首轮立即执行）+ 轮询断言 + StopAsync，
/// 在 Testcontainers 真实库上运行，DI 组合与生产一致（审计/告警/调度锁/系统配置）。
/// </summary>
[Collection("postgres")]
public class RetentionCleanupWorkerTests : IAsyncLifetime
{
    /// <summary>V005 种子管理员（retention_locks.locked_by 外键需要真实用户）</summary>
    private static readonly Guid AdminUserId = Guid.Parse("c0000001-0000-0000-0000-000000000001");

    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    /// <summary>
    /// 本测试实例的仓库根。物理删除围栏要求待删目录位于当前仓库根之内，
    /// 因此每个用例都把 system_settings.repository_path 指到这里，
    /// 并让 MakeRepositoryDirectory 在其下建目录。
    /// </summary>
    private string _repositoryRoot = null!;

    public RetentionCleanupWorkerTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "bm-ret-test", Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(_repositoryRoot);

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<IScheduledLockService, ScheduledLockService>();
        sc.AddScoped<IUploadStorage, UploadStorage>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();

        // 必须先于 worker 首轮执行写入：SystemSettingsProvider 缓存 60 秒，
        // 而每个用例都会新建一次 ServiceProvider，缓存此时还是空的。
        await SetRepositoryRootSettingAsync(_repositoryRoot);
    }

    /// <summary>把 system_settings.repository_path 指向本用例的临时仓库根</summary>
    private async Task SetRepositoryRootSettingAsync(string root)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE system_settings
            SET setting_value = to_jsonb({root}::text)
            WHERE setting_key = 'repository_path'
            """);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- 用例一：GFS 保留 + 最短保留期边界 + 最后副本保护 ----------

    /// <summary>
    /// 策略 KeepLastCount=1 / MinimumRetentionDays=30：
    /// 最新（-5d）是最后可用副本必须保留；-10d 在最短保留期内保留；-40d 超期且未被 GFS 保留 → 回收站。
    /// </summary>
    [Fact]
    public async Task Gfs保留_最后副本与保留期边界()
    {
        var now = DateTime.UtcNow;
        Guid setIdNewest, setIdMiddle, setIdOld;
        Guid taskId;

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var policy = new RetentionPolicy
            {
                Id = Guid.NewGuid(),
                Name = $"it3-gfs-{Guid.NewGuid():N}",
                KeepLastCount = 1,
                KeepWeeklyCount = null,
                KeepMonthlyCount = null,
                KeepYearlyCount = null,
                MinimumRetentionDays = 30,
                RecycleBinDays = 7,
                CreatedAt = now,
                UpdatedAt = now
            };
            (var clientId, taskId) = await SeedClientAndTaskAsync(db, policy, now);

            setIdNewest = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-5), now);
            setIdMiddle = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-10), now);
            setIdOld = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-40), now);
            await db.SaveChangesAsync();
        }

        await RunWorkerUntilAsync(async () =>
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.BackupSets.AsNoTracking().AnyAsync(s => s.Id == setIdOld && s.Status == BackupSetStatus.RecycleBin);
        });

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var newest = await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setIdNewest);
            var middle = await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setIdMiddle);
            var old = await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setIdOld);

            // 最后可用副本不得删除
            Assert.Equal(BackupSetStatus.Available, newest.Status);
            // 最短保留期内（30 天）不得回收
            Assert.Equal(BackupSetStatus.Available, middle.Status);
            // 超期且未被 GFS 保留 → 回收站，并带回收期限
            Assert.Equal(BackupSetStatus.RecycleBin, old.Status);
            Assert.NotNull(old.RetentionUntil);
            Assert.InRange(old.RetentionUntil!.Value, now.AddDays(6), now.AddDays(8));

            // 审计只应记录被回收的那一条
            var recycledAudits = await db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == "retention.recycle" && a.ResourceId == setIdOld)
                .CountAsync();
            Assert.Equal(1, recycledAudits);
            Assert.False(await db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.Action == "retention.recycle" &&
                    (a.ResourceId == setIdNewest || a.ResourceId == setIdMiddle)));
        }
    }

    // ---------- 用例二：锁定必须阻止回收 ----------

    /// <summary>
    /// MinimumRetentionDays=1（全部超期且无 GFS 保留名额）：
    /// 活动保留锁 / locked 字段阻止回收；到期锁与失效锁不阻止；无锁集合被回收作对照。
    /// </summary>
    [Fact]
    public async Task 保留锁与字段锁阻止回收()
    {
        var now = DateTime.UtcNow;
        Guid idActiveLock, idExpiredLock, idInactiveLock, idFieldLocked, idPlain;

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var policy = new RetentionPolicy
            {
                Id = Guid.NewGuid(),
                Name = $"it3-lock-{Guid.NewGuid():N}",
                KeepLastCount = null,
                KeepWeeklyCount = null,
                KeepMonthlyCount = null,
                KeepYearlyCount = null,
                MinimumRetentionDays = 1,
                RecycleBinDays = 7,
                CreatedAt = now,
                UpdatedAt = now
            };
            (var clientId, var taskId) = await SeedClientAndTaskAsync(db, policy, now);

            var uploadedAt = now.AddDays(-3);
            idActiveLock = await SeedBackupSetAsync(db, clientId, taskId, uploadedAt, now);
            idExpiredLock = await SeedBackupSetAsync(db, clientId, taskId, uploadedAt, now);
            idInactiveLock = await SeedBackupSetAsync(db, clientId, taskId, uploadedAt, now);
            idFieldLocked = await SeedBackupSetAsync(db, clientId, taskId, uploadedAt, now, locked: true);
            idPlain = await SeedBackupSetAsync(db, clientId, taskId, uploadedAt, now);

            db.RetentionLocks.Add(new RetentionLock
            {
                Id = Guid.NewGuid(),
                BackupSetId = idActiveLock,
                Reason = "诉讼保全",
                LockedBy = AdminUserId,
                LockedAt = now,
                ExpiresAt = null,          // 永久锁
                Active = true
            });
            db.RetentionLocks.Add(new RetentionLock
            {
                Id = Guid.NewGuid(),
                BackupSetId = idExpiredLock,
                Reason = "已过期的锁",
                LockedBy = AdminUserId,
                LockedAt = now.AddDays(-2),
                ExpiresAt = now.AddHours(-1),   // 已到期 → 不阻止
                Active = true
            });
            db.RetentionLocks.Add(new RetentionLock
            {
                Id = Guid.NewGuid(),
                BackupSetId = idInactiveLock,
                Reason = "已解除的锁",
                LockedBy = AdminUserId,
                LockedAt = now.AddDays(-2),
                ExpiresAt = null,
                Active = false             // 未生效 → 不阻止
            });
            await db.SaveChangesAsync();
        }

        await RunWorkerUntilAsync(async () =>
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.BackupSets.AsNoTracking().AnyAsync(s => s.Id == idPlain && s.Status == BackupSetStatus.RecycleBin);
        });

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(BackupSetStatus.Available,
                await StatusAsync(db, idActiveLock));     // 永久活动锁 → 保留
            Assert.Equal(BackupSetStatus.RecycleBin,
                await StatusAsync(db, idExpiredLock));    // 到期锁 → 回收
            Assert.Equal(BackupSetStatus.RecycleBin,
                await StatusAsync(db, idInactiveLock));   // 失效锁 → 回收
            Assert.Equal(BackupSetStatus.Available,
                await StatusAsync(db, idFieldLocked));    // locked 字段 → 保留
            Assert.Equal(BackupSetStatus.RecycleBin,
                await StatusAsync(db, idPlain));          // 对照：无锁 → 回收
        }
    }

    // ---------- 用例三：回收站到期物理清除 ----------

    /// <summary>
    /// 回收站到期 → 物理删除目录并置 deleted；
    /// 同样到期但被保留锁锁定的不清除；未到期的不清除。
    /// </summary>
    [Fact]
    public async Task 回收站到期物理清除_锁与未到期阻止()
    {
        var now = DateTime.UtcNow;
        Guid idPurge, idLockedInBin, idNotYet;
        var purgeDir = MakeRepositoryDirectory();
        var lockedDir = MakeRepositoryDirectory();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var policy = new RetentionPolicy
            {
                Id = Guid.NewGuid(),
                Name = $"it3-purge-{Guid.NewGuid():N}",
                KeepLastCount = 1,
                MinimumRetentionDays = 30,
                RecycleBinDays = 7,
                CreatedAt = now,
                UpdatedAt = now
            };
            (var clientId, var taskId) = await SeedClientAndTaskAsync(db, policy, now);

            idPurge = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-60), now,
                status: BackupSetStatus.RecycleBin, retentionUntil: now.AddHours(-1), repositoryPath: purgeDir);
            idLockedInBin = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-60), now,
                status: BackupSetStatus.RecycleBin, retentionUntil: now.AddHours(-1), repositoryPath: lockedDir);
            idNotYet = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-60), now,
                status: BackupSetStatus.RecycleBin, retentionUntil: now.AddDays(1), repositoryPath: MakeRepositoryDirectory());

            db.RetentionLocks.Add(new RetentionLock
            {
                Id = Guid.NewGuid(),
                BackupSetId = idLockedInBin,
                Reason = "回收站内仍被锁定",
                LockedBy = AdminUserId,
                LockedAt = now,
                ExpiresAt = null,
                Active = true
            });
            await db.SaveChangesAsync();
        }

        await RunWorkerUntilAsync(async () =>
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.BackupSets.AsNoTracking().AnyAsync(s => s.Id == idPurge && s.Status == BackupSetStatus.Deleted);
        });

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(BackupSetStatus.Deleted, await StatusAsync(db, idPurge));
            Assert.False(Directory.Exists(purgeDir), "到期备份集的仓库目录应被物理删除");

            Assert.Equal(BackupSetStatus.RecycleBin, await StatusAsync(db, idLockedInBin));
            Assert.True(Directory.Exists(lockedDir), "被锁定的备份集目录不得删除");

            Assert.Equal(BackupSetStatus.RecycleBin, await StatusAsync(db, idNotYet));

            Assert.True(await db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.Action == "retention.delete" && a.ResourceId == idPurge));
        }
    }

    // ---------- 用例四：物理删除的仓库根围栏 ----------

    /// <summary>
    /// repository_path 指向仓库根之外时（迁移出错、手工改库、仓库重配都可能造成），
    /// 即便备份集已到期且未被锁定，也必须拒绝递归删除：目录保持存在、状态停在 recycle_bin，
    /// 且不得写出 retention.delete 成功审计。同一轮内围栏之内的备份集应照常被删除，
    /// 以证明拒绝来自围栏本身，而不是这一轮压根没跑。
    /// </summary>
    [Fact]
    public async Task 物理删除_仓库根之外的路径必须拒绝()
    {
        var now = DateTime.UtcNow;
        Guid idInside, idOutside;
        var insideDir = MakeRepositoryDirectory();
        var outsideDir = MakeOutsideRepositoryDirectory();

        try
        {
            await using (var scope = _services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var policy = new RetentionPolicy
                {
                    Id = Guid.NewGuid(),
                    Name = $"it3-fence-{Guid.NewGuid():N}",
                    KeepLastCount = 1,
                    MinimumRetentionDays = 30,
                    RecycleBinDays = 7,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                (var clientId, var taskId) = await SeedClientAndTaskAsync(db, policy, now);

                idInside = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-60), now,
                    status: BackupSetStatus.RecycleBin, retentionUntil: now.AddHours(-1), repositoryPath: insideDir);
                idOutside = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-60), now,
                    status: BackupSetStatus.RecycleBin, retentionUntil: now.AddHours(-1), repositoryPath: outsideDir);
                await db.SaveChangesAsync();
            }

            // 以围栏之内的那个被删除作为"本轮确实执行过"的信号
            await RunWorkerUntilAsync(async () =>
            {
                await using var scope = _services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return await db.BackupSets.AsNoTracking()
                    .AnyAsync(s => s.Id == idInside && s.Status == BackupSetStatus.Deleted);
            });

            await using (var scope = _services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                Assert.Equal(BackupSetStatus.Deleted, await StatusAsync(db, idInside));
                Assert.False(Directory.Exists(insideDir), "围栏之内的到期目录应被物理删除");

                Assert.Equal(BackupSetStatus.RecycleBin, await StatusAsync(db, idOutside));
                Assert.True(Directory.Exists(outsideDir), "仓库根之外的目录绝不允许被删除");
                Assert.True(File.Exists(Path.Combine(outsideDir, "data.bin")), "越界目录的内容必须原样保留");

                Assert.False(await db.AuditLogs.AsNoTracking()
                    .AnyAsync(a => a.Action == "retention.delete" && a.ResourceId == idOutside),
                    "被围栏拒绝的备份集不得写出删除成功审计");
            }
        }
        finally
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, recursive: true);
        }
    }

    // ---------- 基础设施 ----------

    /// <summary>启动真实 worker：首轮立即执行；轮询断言直至成立或超时，随后停止</summary>
    private async Task RunWorkerUntilAsync(Func<Task<bool>> assertion, int timeoutSeconds = 30)
    {
        var worker = new RetentionCleanupWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<RetentionCleanupWorker>>());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (await assertion())
                    return;
                await Task.Delay(500);
            }

            Assert.True(await assertion(), "等待清理结果超时，期望状态未出现");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<BackupSetStatus> StatusAsync(AppDbContext db, Guid id)
        => (await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == id)).Status;

    /// <summary>在本用例的仓库根之下创建备份集目录（同时满足"至少两级"守卫与仓库根围栏）</summary>
    private string MakeRepositoryDirectory()
    {
        var dir = Path.Combine(_repositoryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "data.bin"), "retention-test");
        return dir;
    }

    /// <summary>在仓库根之外创建目录，用于验证物理删除围栏会拒绝越界路径</summary>
    private static string MakeOutsideRepositoryDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bm-ret-outside", Guid.NewGuid().ToString("N"), "stray");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "data.bin"), "must-not-be-deleted");
        return dir;
    }

    /// <summary>种子：策略 + 活动客户端 + 绑定策略的任务</summary>
    private static async Task<(Guid ClientId, Guid TaskId)> SeedClientAndTaskAsync(
        AppDbContext db, RetentionPolicy policy, DateTime now)
    {
        db.RetentionPolicies.Add(policy);

        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"it3-{suffix}",
            Hostname = $"it3-host-{suffix[..8]}",
            DisplayName = $"保留测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"it3-task-{suffix[..8]}",
            ApplicationName = "RetentionTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            RetentionPolicyId = policy.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return (client.Id, task.Id);
    }

    /// <summary>种子：可用（或指定状态）备份集；每个备份集配独立候选（uq_backup_sets_candidate）</summary>
    private static async Task<Guid> SeedBackupSetAsync(
        AppDbContext db, Guid clientId, Guid taskId, DateTime uploadedAt, DateTime now,
        bool locked = false, BackupSetStatus status = BackupSetStatus.Available,
        DateTime? retentionUntil = null, string? repositoryPath = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateKey = $"it3-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = uploadedAt,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = 12,
            CreatedAt = uploadedAt,
            UpdatedAt = uploadedAt
        };
        db.CandidateBackupSets.Add(candidate);
        await db.SaveChangesAsync();

        // backup_sets.upload_session_id 为 NOT NULL 外键，必须配真实会话行
        var session = new BackupMonitor.Core.Entities.Upload.UploadSession
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateBackupSetId = candidate.Id,
            Status = UploadStatus.Committed,
            TotalFiles = 1,
            TotalBytes = 12,
            StartedAt = uploadedAt,
            LastActivityAt = uploadedAt,
            CompletedAt = uploadedAt,
            CommittedAt = uploadedAt,
            CreatedAt = uploadedAt,
            UpdatedAt = uploadedAt
        };
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync();

        var set = new BackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            SourceCandidateId = candidate.Id,
            UploadSessionId = session.Id,
            BackupSetCode = $"BS-IT3-{suffix[..12]}",
            Status = status,
            DiscoveredAt = uploadedAt,
            UploadedAt = uploadedAt,
            VerifiedAt = uploadedAt,
            RepositoryPath = repositoryPath,
            TotalFiles = 1,
            TotalBytes = 12,
            Locked = locked,
            RetentionUntil = retentionUntil,
            CreatedAt = uploadedAt
        };
        db.BackupSets.Add(set);
        await db.SaveChangesAsync();
        return set.Id;
    }
}
