using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 每日摘要推送（整改清单 R8）。
///
/// 日报要证明的是「这套系统本身还活着」——告警只在坏的时候响，
/// 一个星期没收到任何告警，可能是一切正常，也可能是服务端三天前就死了。
///
/// 这几条钉的是它的三个要害：
///   1. **「一切正常」也要发**。只在有事时发的日报退化成另一种告警，就失去了全部意义。
///   2. 同一个业务日只发一次。服务端重启是常事，靠进程内存记「今天发过了」
///      会让「早上重启一次」变成「收到两封日报」。
///   3. 关掉之后真的不发。
/// </summary>
[Collection("postgres")]
public class DailyDigestTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public DailyDigestTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        // 通知配置里的凭据是加密存的，NotificationService 因此依赖 ISecretProtector。
        // 用临时密钥环：这一组测的是日报有没有发出来，不是密钥持久化。
        sc.AddDataProtection().UseEphemeralDataProtectionProvider();
        sc.AddSingleton<BackupMonitor.Infrastructure.Security.ISecretProtector,
            BackupMonitor.Infrastructure.Security.SecretProtector>();
        sc.AddScoped<INotificationService, NotificationService>();
        sc.AddScoped<IScheduledLockService, ScheduledLockService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();

        // 每条用例都从「没发过任何日报」的状态开始：这个键是全库共享的幂等标记，
        // 上一条用例留下的值会让下一条什么都不做。
        await ClearLastSentAsync();
        await ConfigureEmailChannelAsync();

        // 发送时刻钉死成 0 点。不钉的话这一组用例是「看钟点」的：
        // 默认发送时刻是 8 点，测试配置里 Reports:Timezone 为空、按 UTC 解析，
        // 于是 UTC 00:00~07:59 之间跑，RunOnceAsync 会在「还没到点」那道守卫上直接返回——
        // 「一切正常也发日报」必然失败，而另外两条（重复执行、关掉后不发）会**空过**：
        // 什么都没发生，断言照样成立。后者比前者更危险，它让人以为这两条一直在把关。
        await SetSettingAsync(DailyDigestWorker.HourKey, "0");
        return;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>日报的意义就是证明系统还活着——没出事的那天同样要发。</summary>
    [Fact]
    public async Task 一切正常时也发日报()
    {
        var before = await DigestDeliveryCountAsync();

        await RunAsync();

        var after = await DigestDeliveryCountAsync();
        Assert.True(after > before, "一切正常的那天也必须产生日报投递");

        // 这一条钉的就是「无条件发」：整个 postgres 测试集合共用一个库，
        // 别的用例会留下离线客户端和失败会话，所以断言标题里出现「一切正常」
        // 会被这些残留污染——而那也不是本条要证明的事。
        // 要证明的是：不管昨天有事没事，日报都产生了投递。
        var latest = await LatestDigestAsync();
        Assert.Contains("备份日报", latest.Subject!);
        // 正文里必须写清楚「收不到它就说明服务端可能停了」——那是日报唯一无可替代的作用
        Assert.Contains("连续收不到", latest.Body!);
        // 「一切正常」那一支不能只存在于代码里：它是这条整改的核心，单独钉一次。
        Assert.Contains("一切正常", DailyDigestWorker.SubjectSuffixFor(
            new DailyDigestWorker.DigestData(3, 1024, 0, 0, 0, 0, 0)));
    }

    /// <summary>同一个业务日只发一次：服务端一天重启几次不该变成几封日报。</summary>
    [Fact]
    public async Task 同一天重复执行不会重复发送()
    {
        await RunAsync();
        var afterFirst = await DigestDeliveryCountAsync();

        await RunAsync();
        await RunAsync();

        Assert.Equal(afterFirst, await DigestDeliveryCountAsync());
    }

    /// <summary>显式关掉之后真的不发——开关不生效比没有开关更糟。</summary>
    [Fact]
    public async Task 关闭后不再发送()
    {
        await SetSettingAsync(DailyDigestWorker.EnabledKey, "false");
        try
        {
            var before = await DigestDeliveryCountAsync();
            await RunAsync();
            Assert.Equal(before, await DigestDeliveryCountAsync());
        }
        finally
        {
            await SetSettingAsync(DailyDigestWorker.EnabledKey, "true");
        }
    }

    // ---------- 基础设施 ----------

    private async Task RunAsync()
    {
        var worker = new DailyDigestWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<DailyDigestWorker>.Instance);

        using var scope = _services.CreateScope();
        await worker.RunOnceAsync(scope, CancellationToken.None);
    }

    /// <summary>日报投递的判据：alert_id 为空且自带标题——告警投递两者都不满足。</summary>
    private async Task<int> DigestDeliveryCountAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.NotificationDeliveries.AsNoTracking()
            .CountAsync(d => d.AlertId == null && d.Subject != null);
    }

    private async Task<(string? Subject, string? Body)> LatestDigestAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.AlertId == null && d.Subject != null)
            .OrderByDescending(d => d.Id)
            .Select(d => new { d.Subject, d.Body })
            .FirstAsync();
        return (row.Subject, row.Body);
    }

    private async Task ClearLastSentAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM system_settings WHERE setting_key = {DailyDigestWorker.LastSentDateKey}
            """);
        scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>().Invalidate();
    }

    private async Task SetSettingAsync(string key, string jsonValue)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
            VALUES ({0}, {1}::jsonb, false, now())
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """.Replace("{0}", "@p0").Replace("{1}", "@p1"),
            new Npgsql.NpgsqlParameter("p0", key),
            new Npgsql.NpgsqlParameter("p1", jsonValue));
        scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>().Invalidate();
    }

    /// <summary>没有启用的渠道就不会产生任何投递，这一组用例也就无从断言。</summary>
    private async Task ConfigureEmailChannelAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        await notifications.UpdateSettingsAsync(new NotificationSettingsDto
        {
            Email = new EmailChannelSettingsDto
            {
                Enabled = true,
                SmtpHost = "smtp.example.local",
                SmtpPort = 25,
                FromAddress = "backup@example.local",
                Recipients = ["ops@example.local"]
            }
        });
    }
}
