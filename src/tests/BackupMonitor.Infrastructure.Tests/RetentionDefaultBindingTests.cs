using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 默认保留策略：NULL = 跟随默认（F2），以及默认策略可在界面上切换（F1）。
///
/// 背景：清理器原先只处理 retention_policy_id 非空的任务，而这一项在任务表单的高级折叠区里
/// 默认留空——两件事凑一起，结果是按默认值建出来的任务永远不清理、仓库无限增长。
/// V027 的对策是建任务时把默认策略 ID 抄一份写进任务，堵住了漏洞，但也把「默认」变成了
/// 建任务那一刻的一次性快照：之后改默认策略，存量任务纹丝不动。
///
/// 现在改成把语义倒过来：留 NULL 就是「跟随默认」，回落发生在清理器读取的时候。
/// 因此这里要证明的是两件事——建任务真的留 NULL；留 NULL 的任务照样会被清理。
/// </summary>
[Collection("postgres")]
public class RetentionDefaultBindingTests : IAsyncLifetime
{
    /// <summary>V027 预置的「只留最近 7 份」，也是 default_retention_policy_id 的种子值</summary>
    private static readonly Guid SeedDefaultPolicyId = Guid.Parse("d0000002-0000-0000-0000-000000000007");

    /// <summary>V027 预置的「只留最近 3 份」，用于验证默认策略可以被换掉</summary>
    private static readonly Guid AltPolicyId = Guid.Parse("d0000002-0000-0000-0000-000000000003");

    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;
    private string _repositoryRoot = null!;

    public RetentionDefaultBindingTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "bm-retdef-test", Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(_repositoryRoot);

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<ICommandDispatcher, UnusedCommandDispatcher>();
        sc.AddScoped<IAgentConfigService, AgentConfigService>();
        sc.AddScoped<IScheduledLockService, ScheduledLockService>();
        sc.AddScoped<IUploadStorage, UploadStorage>();
        // 与生产一致注册成单例（DependencyInjection.cs:24）。这一条不是摆设：
        // 「设为默认」之后必须靠 Invalidate() 让 60 秒缓存立刻作废，
        // 注册成 Scoped 的话每个 scope 各有一份空缓存，这个缺陷就测不出来了。
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddScoped<IBackupTaskService, BackupTaskService>();
        sc.AddScoped<IRetentionPolicyService, RetentionPolicyService>();
        _services = sc.BuildServiceProvider();

        // 物理删除围栏以仓库根为准：指到本用例的临时目录，避免清理器碰到别处的路径
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE system_settings
            SET setting_value = to_jsonb({_repositoryRoot}::text)
            WHERE setting_key = 'repository_path'
            """);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>没选保留策略 → 留 NULL（= 跟随默认），不再把默认策略 ID 抄进任务</summary>
    [Fact]
    public async Task 建任务不选策略时留空表示跟随默认()
    {
        var clientId = await SeedClientAsync();

        await using var scope = _services.CreateAsyncScope();
        var tasks = scope.ServiceProvider.GetRequiredService<IBackupTaskService>();
        var detail = await tasks.CreateAsync(NewRequest(clientId));

        Assert.Null(detail.RetentionPolicyId);
    }

    /// <summary>显式选了某份策略 → 按人选的来，不受默认策略影响</summary>
    [Fact]
    public async Task 显式指定的策略不会被默认策略覆盖()
    {
        var clientId = await SeedClientAsync();

        await using var scope = _services.CreateAsyncScope();
        var tasks = scope.ServiceProvider.GetRequiredService<IBackupTaskService>();
        var request = NewRequest(clientId);
        request.RetentionPolicyId = AltPolicyId;
        var detail = await tasks.CreateAsync(request);

        Assert.Equal(AltPolicyId, detail.RetentionPolicyId);
    }

    /// <summary>
    /// F2 的核心：策略留 NULL 的任务照样被清理，按的是当前默认策略。
    /// 默认策略「只留最近 7 份」+ 8 份备份 → 最旧那份进回收站，任务本身仍然是 NULL
    /// （回落发生在清理器读取时，不会反过来把 ID 写回任务）。
    /// </summary>
    [Fact]
    public async Task 跟随默认的任务按默认策略清理()
    {
        var now = DateTime.UtcNow;
        Guid taskId;
        Guid oldestSetId;

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clientId = await SeedClientAsync();
            taskId = await SeedTaskWithoutPolicyAsync(db, clientId, now);

            // 8 份：默认策略留最近 7 份，只有最旧的一份会被回收（12.5% 远低于 50% 熔断线）
            oldestSetId = await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-80));
            for (var i = 7; i >= 1; i--)
                await SeedBackupSetAsync(db, clientId, taskId, now.AddDays(-i * 2));
        }

        await RunWorkerUntilAsync(async () =>
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var set = await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == oldestSetId);
            return set.Status == BackupSetStatus.RecycleBin;
        });

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 清理器不该顺手把默认策略 ID 写回任务：一写回去，「跟随默认」又变回一次性快照
            var task = await db.BackupTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
            Assert.Null(task.RetentionPolicyId);

            // 保留下来的 7 份一份都不能少
            var remaining = await db.BackupSets.AsNoTracking()
                .CountAsync(s => s.TaskId == taskId && s.Status == BackupSetStatus.Available);
            Assert.Equal(7, remaining);
        }
    }

    /// <summary>
    /// F1：默认策略能在接口上换掉，而且立刻生效。
    /// 「立刻」是这条用例真正要盯的东西——SystemSettingsProvider 是单例、缓存 60 秒，
    /// SetDefaultAsync 里漏掉 Invalidate() 的话，这里先读一次列表把缓存填热，
    /// 切换之后再读到的仍是旧默认，界面上表现为「设置没保存」。
    /// </summary>
    [Fact]
    public async Task 切换默认策略立即生效()
    {
        await using var scope = _services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRetentionPolicyService>();

        // 先读一次，把 60 秒缓存填热
        var before = await policies.GetListAsync();
        Assert.Equal(SeedDefaultPolicyId, Assert.Single(before, p => p.IsDefault).Id);

        try
        {
            var changed = await policies.SetDefaultAsync(AltPolicyId);
            Assert.True(changed.IsDefault);

            var after = await policies.GetListAsync();
            Assert.Equal(AltPolicyId, Assert.Single(after, p => p.IsDefault).Id);

            // 换过默认之后，原来那份不再被「它是默认策略」这条理由挡住删除
            // （这里不真的删——预置策略后面的用例还要用，只确认它不再是默认）
            Assert.DoesNotContain(after, p => p.Id == SeedDefaultPolicyId && p.IsDefault);
        }
        finally
        {
            // 同一个库被整个 postgres 测试集合共用，默认策略必须还原，
            // 否则「预置策略随迁移装好且默认策略被标出」会随执行顺序时好时坏
            await policies.SetDefaultAsync(SeedDefaultPolicyId);
        }
    }

    /// <summary>默认策略被删掉的话，跟随默认的任务会退回「不清理」，因此必须挡住</summary>
    [Fact]
    public async Task 默认策略不能被删除()
    {
        await using var scope = _services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRetentionPolicyService>();

        var ex = await Assert.ThrowsAsync<BackupMonitor.Shared.Exceptions.BusinessException>(
            () => policies.DeleteAsync(SeedDefaultPolicyId));
        Assert.Equal(409, ex.StatusCode);
    }

    /// <summary>预置策略必须真的装进库里，否则任务表单里还是只有那份 GFS</summary>
    [Fact]
    public async Task 预置策略随迁移装好且默认策略被标出()
    {
        await using var scope = _services.CreateAsyncScope();
        var policies = await scope.ServiceProvider.GetRequiredService<IRetentionPolicyService>().GetListAsync();

        Assert.Contains(policies, p => p.Name == "只留最近 3 份" && p.KeepLastCount == 3);
        Assert.Contains(policies, p => p.Name == "只留最近 7 份" && p.KeepLastCount == 7);
        Assert.Contains(policies, p => p.Name == "日备留 7 天 + 月末留 12 个月" && p.KeepMonthlyCount == 12);

        // 最短保留天数会盖过份数设置，预置策略一律 0
        Assert.All(policies.Where(p => p.Id.ToString().StartsWith("d0000002")),
            p => Assert.Equal(0, p.MinimumRetentionDays));

        var single = Assert.Single(policies, p => p.IsDefault);
        Assert.Equal(SeedDefaultPolicyId, single.Id);
    }

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

            Assert.True(await assertion(), "等待清理结果超时：跟随默认的任务没有按默认策略被清理");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private async Task<Guid> SeedClientAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = Guid.NewGuid().ToString("N"),
            Hostname = "RETDEF-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "默认策略测试客户端",
            Status = ClientStatus.Online
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    /// <summary>种子：retention_policy_id 留 NULL 的任务（= 跟随默认）</summary>
    private static async Task<Guid> SeedTaskWithoutPolicyAsync(AppDbContext db, Guid clientId, DateTime now)
    {
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "跟随默认-" + Guid.NewGuid().ToString("N")[..8],
            ApplicationName = "RetentionDefaultTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            RetentionPolicyId = null,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    /// <summary>种子：可用备份集（候选与上传会话都是 NOT NULL 外键，必须配齐真实行）</summary>
    private static async Task<Guid> SeedBackupSetAsync(
        AppDbContext db, Guid clientId, Guid taskId, DateTime uploadedAt)
    {
        var suffix = Guid.NewGuid().ToString("N");

        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateKey = $"retdef-{suffix}",
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
            BackupSetCode = $"BS-RETDEF-{suffix[..12]}",
            Status = BackupSetStatus.Available,
            DiscoveredAt = uploadedAt,
            UploadedAt = uploadedAt,
            VerifiedAt = uploadedAt,
            TotalFiles = 1,
            TotalBytes = 12,
            CreatedAt = uploadedAt
        };
        db.BackupSets.Add(set);
        await db.SaveChangesAsync();
        return set.Id;
    }

    private static CreateBackupTaskRequest NewRequest(Guid clientId) => new()
    {
        ClientId = clientId,
        Name = "默认策略测试任务-" + Guid.NewGuid().ToString("N")[..8],
        ApplicationName = "SQLServer",
        SourcePath = @"D:\backup\test",
        RecognizerType = "multi_file_set",
        TaskMode = "automatic"
    };
}
