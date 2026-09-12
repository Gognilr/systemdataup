using BackupMonitor.Core.Entities.Alert;
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
/// 告警恢复通知。
///
/// 这一组盯的是「发给谁」。发多了比不发更糟：一条从没发出去的告警
/// （抖动、被静默、被渠道筛选挡下）突然来一条「已恢复」，收件人只会困惑——
/// 他从来不知道有什么坏掉过，而这种消息看多了，真正的告警也会跟着失去分量。
/// </summary>
[Collection("postgres")]
public class AlertRecoveryNoticeTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public AlertRecoveryNoticeTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
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
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>
    /// 通知真的发出去过，恢复时才补一条。
    ///
    /// 这是这个功能存在的理由：「没有新消息」在收件人那里读起来和「已经好了」
    /// 是一样的，而这两者差别很大。
    /// </summary>
    [Fact]
    public async Task 发出去过的告警恢复时补一条已恢复()
    {
        var key = NewKey();
        await RaiseAsync(key);
        await MarkDeliveriesSentAsync(key);

        await RecoverAsync(key);

        var recovery = await DeliveriesAsync(key, NotificationStatus.Pending);
        Assert.NotEmpty(recovery);
        Assert.All(recovery, d => Assert.Equal("【已恢复】", d.TitlePrefix));
    }

    /// <summary>
    /// 抖动恢复不发。
    ///
    /// 告警建了、投递也落了单，但还躺在队列里就被取消了——没有任何人听说过它坏过。
    /// 这时候来一条「已恢复」，收件人只会问：恢复什么？
    /// </summary>
    [Fact]
    public async Task 一次都没发出去的告警恢复时不发()
    {
        var key = NewKey();
        await RaiseAsync(key);

        // 不把投递标成已发送，直接恢复：模拟「还没轮到派发器就好了」
        await RecoverAsync(key);

        Assert.Empty(await DeliveriesAsync(key, NotificationStatus.Pending));
    }

    /// <summary>
    /// 静默期间不发恢复。
    ///
    /// 维护窗口里机器起起落落是正常的，每恢复一次就响一声，静默就等于没静默。
    /// </summary>
    [Fact]
    public async Task 静默期间恢复不发通知()
    {
        var key = NewKey();
        await RaiseAsync(key);
        await MarkDeliveriesSentAsync(key);
        await SilenceAsync(key);

        await RecoverAsync(key);

        Assert.Empty(await DeliveriesAsync(key, NotificationStatus.Pending));
    }

    /// <summary>开关关掉就不发——它确实会让消息量接近翻倍，得留得住这个选择。</summary>
    [Fact]
    public async Task 开关关掉后不发恢复通知()
    {
        var key = NewKey();
        await RaiseAsync(key);
        await MarkDeliveriesSentAsync(key);
        await SetRecoveryNotifyAsync(false);

        try
        {
            await RecoverAsync(key);
            Assert.Empty(await DeliveriesAsync(key, NotificationStatus.Pending));
        }
        finally
        {
            await SetRecoveryNotifyAsync(true);
        }
    }

    private static string NewKey() => $"test:{Guid.NewGuid():N}:recovery";

    private async Task RaiseAsync(string key)
    {
        await EnsureDingtalkChannelAsync();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAlertingService>()
            .RaiseAsync(key, AlertLevel.Critical, "system", "测试告警", "正文");
    }

    private async Task RecoverAsync(string key)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAlertingService>().RecoverAsync(key);
    }

    private async Task<List<NotificationDelivery>> DeliveriesAsync(string key, NotificationStatus status)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Alert != null && d.Alert.AlertKey == key && d.Status == status)
            .ToListAsync();
    }

    private async Task MarkDeliveriesSentAsync(string key)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = await db.Alerts.Where(a => a.AlertKey == key).Select(a => a.Id).ToListAsync();
        await db.NotificationDeliveries
            .Where(d => d.AlertId != null && ids.Contains(d.AlertId.Value))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, NotificationStatus.Sent)
                .SetProperty(d => d.SentAt, DateTime.UtcNow));
    }

    private async Task SilenceAsync(string key)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // created_by 有到 users 的外键，随便塞个 Guid.Empty 会被数据库挡下来，
        // 而那种失败会伪装成「断言没过」——实际上根本没跑到断言。
        var userId = await db.Users.AsNoTracking().Select(u => u.Id).FirstAsync();

        db.AlertSilences.Add(new AlertSilence
        {
            Id = Guid.NewGuid(),
            AlertKeyPattern = key,
            Reason = "测试静默",
            Until = DateTime.UtcNow.AddHours(1),
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SetRecoveryNotifyAsync(bool enabled)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
            VALUES ({0}, {1}::jsonb, false, now())
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """,
            AlertingService.RecoveryNotifyKey,
            enabled ? "true" : "false");

        scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>().Invalidate();
    }

    /// <summary>至少要有一个启用的渠道，否则 RaiseAsync 根本不会落投递，整组都空过。</summary>
    private async Task EnsureDingtalkChannelAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
            VALUES ('notification_channels', {0}::jsonb, false, now())
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """,
            """
            {"dingtalk":{"enabled":true,"webhookUrl":"https://oapi.dingtalk.com/robot/send?access_token=T",
             "filter":{"minLevel":"notice","excludedCategories":[]}}}
            """);
    }
}
