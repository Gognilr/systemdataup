using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 计划驱动的任务不能掉出监控（D2）。
///
/// 挂进备份计划的任务，它自己的 cron 在下发配置时被抹成 null（改由服务端计划触发）。
/// 这带出两个缺口：停用计划之后没有任何一方再触发它（计划不跑、cron 也没了），
/// 而漏备份巡检只认「有 cron 的任务」，于是备份停了好几天系统一声不响。
/// 这里分别钉住两条：停用计划要把 cron 还回去；启用中的计划要参与漏备份判定。
/// </summary>
[Collection("postgres")]
public class PlanDrivenMissedBackupTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public PlanDrivenMissedBackupTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        // AgentConfigService 依赖 CommandSigner，而它构造时就要一把真实的 RSA 私钥
        sc.AddSingleton(SigningConfiguration());
        sc.AddSingleton<CommandSigner>();
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<IScheduledLockService, ScheduledLockService>();
        sc.AddScoped<IAgentConfigService, AgentConfigService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- 缺口一：停用计划 = 静默停掉备份 ----------

    [Fact]
    public async Task 停用计划后任务的扫描计划重新下发给Agent()
    {
        var cron = "0 3 * * *";
        var (clientId, taskId, planId) = await SeedPlanWithTaskAsync(planEnabled: true, cron: cron);

        // 计划启用中：由服务端驱动，任务自己的 cron 不下发，否则一天会跑两次
        Assert.Null(await ScanScheduleAsync(clientId, taskId));

        await SetPlanEnabledAsync(planId, enabled: false);

        // 计划停用后没有任何一方在驱动它了，cron 必须还回去——
        // 否则这个任务从此一次都不再备份，而且漏备份巡检也看不见它
        Assert.Equal(cron, await ScanScheduleAsync(clientId, taskId));
    }

    // ---------- 缺口二：计划驱动的任务没有漏备份检测 ----------

    [Fact]
    public async Task 计划驱动的任务超过宽限期未成功时产生漏备份告警()
    {
        // 计划时刻取「4 小时前的那一分钟」并钉在 UTC：默认宽限期 120 分钟，
        // 相对当前时刻取值保证任何时候跑都稳定越过宽限期。
        var (_, taskId, _) = await SeedPlanWithTaskAsync(
            planEnabled: true, cron: null, runAt: DateTime.UtcNow.AddHours(-4).TimeOfDay);

        await RunMissedBackupPassAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == $"task:{taskId}:missed");

        Assert.Equal(AlertStatus.Open, alert.Status);
        Assert.Equal("backup_missed", alert.Category);
    }

    /// <summary>
    /// 计划已停用时不判漏备份：那些任务的 cron 已经还给它们自己了，
    /// 按 cron 的分支会接管，这里再判一次会得到按错误时刻算出的重复结论。
    /// </summary>
    [Fact]
    public async Task 停用计划下的任务不按计划时刻判漏备份()
    {
        var (_, taskId, _) = await SeedPlanWithTaskAsync(
            planEnabled: false, cron: null, runAt: DateTime.UtcNow.AddHours(-4).TimeOfDay);

        await RunMissedBackupPassAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Alerts.AsNoTracking().AnyAsync(
            a => a.AlertKey == $"task:{taskId}:missed" && a.Status == AlertStatus.Open));
    }

    // ---------- 基础设施 ----------

    private async Task<string?> ScanScheduleAsync(Guid clientId, Guid taskId)
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAgentConfigService>();
        var config = await svc.GetConfigAsync(clientId, 0);
        return config.Tasks.Single(t => t.TaskId == taskId).ScanSchedule;
    }

    private async Task SetPlanEnabledAsync(Guid planId, bool enabled)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plan = await db.BackupPlans.SingleAsync(p => p.Id == planId);
        plan.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    private async Task RunMissedBackupPassAsync()
    {
        var worker = new MissedBackupWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<MissedBackupWorker>>());
        using var scope = _services.CreateScope();
        await worker.RunPassAsync(scope, CancellationToken.None);
    }

    /// <summary>CommandSigner 需要一把真实的 RSA 私钥才能构造；测试里现造一把即可。</summary>
    private static IConfiguration SigningConfiguration()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:CommandSigningPrivateKey"] = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())
            })
            .Build();
    }

    /// <summary>种子：在线客户端 + 一个任务 + 把这个任务收进一个每日计划</summary>
    private async Task<(Guid ClientId, Guid TaskId, Guid PlanId)> SeedPlanWithTaskAsync(
        bool planEnabled, string? cron, TimeSpan? runAt = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"plan-{suffix}",
            Hostname = $"plan-host-{suffix[..8]}",
            DisplayName = $"计划测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"plan-task-{suffix[..8]}",
            ApplicationName = "PlanMissedTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            Enabled = true,
            ScanSchedule = cron,
            ScheduleTimezone = "UTC",
            // 建出来不足一个宽限期的任务不判漏，往前推 30 天避开这条
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now
        };
        var plan = new BackupPlan
        {
            Id = Guid.NewGuid(),
            Name = $"计划-{suffix[..8]}",
            Enabled = planEnabled,
            ScheduleKind = PlanScheduleKind.Daily,
            RunAt = runAt ?? new TimeSpan(3, 0, 0),
            Timezone = "UTC",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        db.BackupPlans.Add(plan);
        await db.SaveChangesAsync();

        db.BackupPlanItems.Add(new BackupPlanItem
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            TaskId = task.Id,
            SortOrder = 0,
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        return (client.Id, task.Id, plan.Id);
    }
}
