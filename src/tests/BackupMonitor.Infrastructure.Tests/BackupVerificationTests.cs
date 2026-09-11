using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 备份完整性校验的两条（D7 / D8）。
///
/// D7：「重新校验」原先借用 status 字段表示「校验中」，带来两个后果——
/// 一是原状态丢了（对人工隔离的备份点一下重新校验，哈希对得上就变回可用，
/// 当初隔离的理由不声不响地消失），二是校验里任何一条提前退出的路径
/// 都会把它永久留在 verifying，那份备份从此既不可用也删不掉。
///
/// D8：校验失败的告警文案一直写着「定期复查没通过」，而系统里根本没有任何东西在定期复查。
/// 承诺了却没做的事比没承诺更糟：人会以为备份的完整性一直有人看着，于是自己再也不去抽查。
/// </summary>
[Collection("postgres")]
public class BackupVerificationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;
    private string _repositoryRoot = null!;

    public BackupVerificationTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "bm-verify-test", Guid.NewGuid().ToString("N"), "repo");
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
        sc.AddKeyedSingleton(QueueKeys.Verify, (_, _) => Channel.CreateUnbounded<WorkItem>());
        sc.AddScoped<IBackupSetService, BackupSetService>();
        _services = sc.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE system_settings
            SET setting_value = to_jsonb({_repositoryRoot}::text)
            WHERE setting_key = 'repository_path'
            """);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- D7 ----------

    /// <summary>
    /// 隔离过的备份不接受「重新校验」。
    ///
    /// 隔离是「我怀疑这一份有问题，先别用也别删」的人工判断。校验只能证明
    /// 「文件和存进来时一样」，而隔离的理由通常正是「存进来的那一份本身就可疑」——
    /// 让它把 quarantined 洗成 available，等于给了一个绕过人工判断的后门。
    /// </summary>
    [Fact]
    public async Task 隔离过的备份不允许重新校验()
    {
        var setId = await SeedSetAsync(BackupSetStatus.Quarantined);

        await using var scope = _services.CreateAsyncScope();
        var sets = scope.ServiceProvider.GetRequiredService<IBackupSetService>();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => sets.VerifyAsync(setId));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("隔离", ex.Message);
    }

    /// <summary>「校验中」用时间戳表示，status 一个字都不动——原状态因此永远不会丢。</summary>
    [Fact]
    public async Task 重新校验不改状态只打时间戳()
    {
        var setId = await SeedSetAsync(BackupSetStatus.VerificationFailed);

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupSetService>().VerifyAsync(setId);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var set = await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setId);

            Assert.NotNull(set.VerifyingSince);
            Assert.Equal(BackupSetStatus.VerificationFailed, set.Status);
        }
    }

    /// <summary>已经在校验中的不接受第二次排队，否则同一份会被重复整读一遍。</summary>
    [Fact]
    public async Task 已经在校验中的不允许再次入队()
    {
        var setId = await SeedSetAsync(BackupSetStatus.Available);

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupSetService>().VerifyAsync(setId);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var sets = scope.ServiceProvider.GetRequiredService<IBackupSetService>();
            var ex = await Assert.ThrowsAsync<BusinessException>(() => sets.VerifyAsync(setId));
            Assert.Equal(409, ex.StatusCode);
        }
    }

    // ---------- D8 ----------

    /// <summary>
    /// 定期复查真的会把最久没看过的那几份排进校验队列，并当场打上「校验中」——
    /// 不打标记的话，下一轮巡检会把同一份再排一次。
    /// </summary>
    [Fact]
    public async Task 定期复查按最旧优先入队并打上校验中()
    {
        var oldest = await SeedSetAsync(BackupSetStatus.Available, verifiedAt: DateTime.UtcNow.AddDays(-60));
        var newer = await SeedSetAsync(BackupSetStatus.Available, verifiedAt: DateTime.UtcNow.AddDays(-30));

        // 每轮只取一份，反复跑直到这两份都被取走。断言的是**它们之间**的先后，
        // 而不是「全库最旧的那一份是我种的这个」——测试库是共用的，
        // 别的用例随时会种进更旧的数据，那样的断言只会变成一条随机失败的测试。
        await SetSettingAsync(BackupReverifyWorker.BatchSizeKey, "1");

        Guid? firstTaken = null;
        for (var i = 0; i < 200 && firstTaken is null; i++)
        {
            if (await RunReverifyPassAsync() == 0)
                break;

            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var taken = await db.BackupSets.AsNoTracking()
                .Where(s => (s.Id == oldest || s.Id == newer) && s.VerifyingSince != null)
                .Select(s => s.Id)
                .ToListAsync();

            if (taken.Count > 0)
                firstTaken = taken[0];
        }

        Assert.Equal(oldest, firstTaken);
    }

    /// <summary>
    /// 隔离的、回收站里的、已删除的都不参与定期复查：
    /// 前者是人工判断先别动它，后两者的文件可能压根不在了。
    /// </summary>
    [Theory]
    [InlineData(BackupSetStatus.Quarantined)]
    [InlineData(BackupSetStatus.RecycleBin)]
    [InlineData(BackupSetStatus.Deleted)]
    public async Task 定期复查跳过隔离与已删除的备份(BackupSetStatus status)
    {
        var setId = await SeedSetAsync(status, verifiedAt: DateTime.UtcNow.AddDays(-365));

        await SetSettingAsync(BackupReverifyWorker.BatchSizeKey, "50");
        await RunReverifyPassAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setId)).VerifyingSince);
    }

    /// <summary>
    /// 关掉之后一个复查工作项都不产生（R18）。
    ///
    /// 原来没有关闭的办法：间隔 clamp 在 1~720 小时，现场真要临时停掉它
    /// 只能把间隔改成 720 小时——那是「关掉」的一个变相写法，而不是关掉。
    /// 开关不生效比没有开关更糟：人以为关了，盘却还在被磨。
    /// </summary>
    [Fact]
    public async Task 停用定期复查后不再产生工作项()
    {
        var setId = await SeedSetAsync(BackupSetStatus.Available, verifiedAt: DateTime.UtcNow.AddDays(-365));
        await SetSettingAsync(BackupReverifyWorker.BatchSizeKey, "50");
        await SetSettingAsync(BackupReverifyWorker.EnabledKey, "false");
        try
        {
            Assert.Equal(0, await RunReverifyPassAsync());

            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null((await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setId)).VerifyingSince);
        }
        finally
        {
            await SetSettingAsync(BackupReverifyWorker.EnabledKey, "true");
        }
    }

    // ---------- 基础设施 ----------

    private async Task<int> RunReverifyPassAsync()
    {
        var worker = new BackupReverifyWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredKeyedService<Channel<WorkItem>>(QueueKeys.Verify),
            _services.GetRequiredService<ILogger<BackupReverifyWorker>>());
        using var scope = _services.CreateScope();
        return await worker.RunPassAsync(scope, CancellationToken.None);
    }

    private async Task SetSettingAsync(string key, string jsonValue)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
            VALUES ({key}, {jsonValue}::jsonb, false, NULL)
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """);
        scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>().Invalidate();
    }

    private async Task<Guid> SeedSetAsync(BackupSetStatus status, DateTime? verifiedAt = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"vf-{suffix}",
            Hostname = $"VF-{suffix[..8]}",
            DisplayName = $"校验测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"vf-task-{suffix[..8]}",
            ApplicationName = "VerifyTest",
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
            CandidateKey = $"vf-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = 12,
            CreatedAt = now,
            UpdatedAt = now
        };
        var session = new Core.Entities.Upload.UploadSession
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            TaskId = task.Id,
            CandidateBackupSetId = candidate.Id,
            Status = UploadStatus.Committed,
            TotalFiles = 1,
            TotalBytes = 12,
            StartedAt = now,
            LastActivityAt = now,
            CompletedAt = now,
            CommittedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        db.CandidateBackupSets.Add(candidate);
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync();

        var repositoryPath = Path.Combine(_repositoryRoot, suffix);
        Directory.CreateDirectory(repositoryPath);

        var set = new BackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            TaskId = task.Id,
            SourceCandidateId = candidate.Id,
            UploadSessionId = session.Id,
            BackupSetCode = $"BS-VF-{suffix[..12]}",
            Status = status,
            DiscoveredAt = now,
            UploadedAt = now.AddDays(-90),
            VerifiedAt = verifiedAt,
            RepositoryPath = repositoryPath,
            TotalFiles = 1,
            TotalBytes = 12,
            CreatedAt = now
        };
        db.BackupSets.Add(set);
        await db.SaveChangesAsync();
        return set.Id;
    }
}
