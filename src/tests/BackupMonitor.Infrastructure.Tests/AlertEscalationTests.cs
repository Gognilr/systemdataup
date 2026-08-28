using System.Text.Json;
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
/// 告警语义集成测试（审计 G-11 / G-12 / G-13）。
///
/// 三条共同指向一件事：告警模型只有「新建」和「恢复」两个通知触发点，
/// 而真实运维关心的是三件事——出现、恶化、恢复。
/// 「事情变严重了」这个最该被推送的信号，恰好是唯一不推送的。
/// </summary>
[Collection("postgres")]
public class AlertEscalationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public AlertEscalationTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddScoped<IScheduledLockService, ScheduledLockService>();
        _services = sc.BuildServiceProvider();

        // 通知渠道必须真的启用，否则 CreateDeliveriesForAsync 一条投递都不落，
        // 「升级要补发一封」这个断言就变成了对空集的断言。
        await EnableEmailChannelAsync();
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- 用例 ----------

    [Fact]
    public async Task 等级提升补发一封带已升级前缀的通知()
    {
        var key = $"test:{Guid.NewGuid():N}:escalate";

        // 第一次：Warning。磁盘可用率 14%
        await RaiseAsync(key, AlertLevel.Warning);
        Assert.Equal(1, await DeliveryCountAsync(key));
        Assert.Null((await DeliveriesAsync(key))[0].TitlePrefix);

        // 掉到 3%：升为 Critical。这一步原先库里等级升上去了，却没有任何人收到通知。
        await RaiseAsync(key, AlertLevel.Critical);
        var afterEscalation = await DeliveriesAsync(key);
        Assert.Equal(2, afterEscalation.Count);
        Assert.Single(afterEscalation, d => d.TitlePrefix == "【已升级】");

        // 同等级重复发生不再补发第三封——托盘和邮箱都不是用来刷屏的
        await RaiseAsync(key, AlertLevel.Critical);
        Assert.Equal(2, await DeliveryCountAsync(key));

        // 等级不会被降回去（Critical=0 数值小即等级高）
        await RaiseAsync(key, AlertLevel.Warning);
        Assert.Equal(2, await DeliveryCountAsync(key));

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == key);
        Assert.Equal(AlertLevel.Critical, alert.Level);
        Assert.Equal(4, alert.OccurrenceCount);
    }

    [Fact]
    public async Task 告警恢复后待发送通知被取消()
    {
        var key = $"test:{Guid.NewGuid():N}:recover";

        await RaiseAsync(key, AlertLevel.Warning);
        Assert.Equal(NotificationStatus.Pending, (await DeliveriesAsync(key))[0].Status);

        // 抖动恢复：这一封还没发出去，就不该再发了
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlertingService>().RecoverAsync(key);
        }

        var after = await DeliveriesAsync(key);
        Assert.All(after, d => Assert.Equal(NotificationStatus.Cancelled, d.Status));

        await using var scope2 = _services.CreateAsyncScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == key);
        Assert.Equal(AlertStatus.Recovered, alert.Status);
    }

    [Fact]
    public async Task 停用任务后漏备份告警被恢复()
    {
        // 排障时最自然的动作就是「先把这个任务停掉再说」——
        // 而这恰好会让那条严重告警永远留在告警中心，只能人工关闭。
        var taskId = await SeedTaskAsync(enabled: true);
        var key = $"task:{taskId}:missed";

        await RaiseAsync(key, AlertLevel.Critical, taskId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BackupTasks.Where(t => t.Id == taskId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Enabled, false));
        }

        await RunMissedBackupPassAsync();

        await using var scope2 = _services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var alert = await db2.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == key);
        Assert.Equal(AlertStatus.Recovered, alert.Status);
    }

    [Fact]
    public async Task 本轮无从判定的任务其漏备份告警同样被恢复()
    {
        // 刚建出来不足一个宽限期的任务命中四处 continue 之一，本轮不会被判定。
        // 按 G-13 的规则它和「任务被停用」是同一类情形：无从判定就不该留着旧告警，
        // 否则那条告警只能人工关闭。
        var taskId = await SeedTaskAsync(enabled: true, createdAt: DateTime.UtcNow);
        var key = $"task:{taskId}:missed";
        await RaiseAsync(key, AlertLevel.Warning, taskId);

        await RunMissedBackupPassAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == key);
        Assert.Equal(AlertStatus.Recovered, alert.Status);
    }

    [Fact]
    public async Task 真的漏了备份的任务告警仍然保持活动()
    {
        // G-13 的反向护栏：轮末的统一恢复绝不能把「刚刚判定为漏备份」的那些一起清掉，
        // 否则这个 worker 就从「会漏报恢复」变成「根本报不出来」。
        var taskId = await SeedTaskAsync(enabled: true);   // 建于 30 天前，每日 02:00，从无成功入库

        await RunMissedBackupPassAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == $"task:{taskId}:missed");
        Assert.Equal(AlertStatus.Open, alert.Status);
        Assert.Equal("backup_missed", alert.Category);
    }

    // ---------- 基础设施 ----------

    private async Task RaiseAsync(string key, AlertLevel level, Guid? taskId = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
        await alerting.RaiseAsync(key, level, "unit_test", "磁盘空间不足", "可用率下降", taskId: taskId);
    }

    private async Task RunMissedBackupPassAsync()
    {
        var worker = new MissedBackupWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<MissedBackupWorker>>());
        using var scope = _services.CreateScope();
        await worker.RunPassAsync(scope, CancellationToken.None);
    }

    private async Task<List<Core.Entities.Alert.NotificationDelivery>> DeliveriesAsync(string alertKey)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Alert!.AlertKey == alertKey)
            .OrderBy(d => d.Id)
            .ToListAsync();
    }

    private async Task<int> DeliveryCountAsync(string alertKey) => (await DeliveriesAsync(alertKey)).Count;

    private async Task EnableEmailChannelAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = new NotificationSettingsDto();
        settings.Email.Enabled = true;
        settings.Email.Recipients.Add("ops@example.com");
        var json = JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
            VALUES ({0}, CAST({1} AS jsonb), false, NULL)
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """, NotificationService.SettingsKey, json);
    }

    private async Task<Guid> SeedTaskAsync(bool enabled, DateTime? createdAt = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"itag-{suffix}",
            Hostname = $"itag-host-{suffix[..8]}",
            DisplayName = $"告警测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"itag-task-{suffix[..8]}",
            ApplicationName = "AlertTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            Enabled = enabled,
            // 计划时刻取「4 小时前的那一分钟」并显式钉在 UTC：写死 "0 2 * * *" 时，
            // 实体默认时区 Asia/Shanghai 会让上一次计划落在中国时间当天 02:00，
            // 于是每天 02:00~04:00 这两小时里它仍在宽限期内 → 判不出漏备份，
            // 「真的漏了备份的任务告警仍然保持活动」在这个时段必然失败。
            // 相对当前时刻取值，任何时候跑都稳定越过宽限期，相邻两次计划仍相隔 24 小时。
            ScanSchedule = $"{now.AddHours(-4).Minute} {now.AddHours(-4).Hour} * * *",
            ScheduleTimezone = "UTC",
            CreatedAt = createdAt ?? now.AddDays(-30),
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }
}
