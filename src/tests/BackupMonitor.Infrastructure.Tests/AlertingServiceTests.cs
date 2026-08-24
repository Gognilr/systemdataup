using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 告警产生与去重回归测试。
///
/// 背景：RaiseAsync 曾有一道方向写反的前置守卫——查不到同键活动告警就直接 return，
/// 于是「新建告警」那条分支成了死代码，全系统任何 RaiseAsync 都不产生告警
/// （入库失败、保留删除失败、回收熔断……监控输出端整体失效）。
/// 该缺陷能长期存活，正是因为告警「产生」这一步完全没有自动化测试覆盖，
/// 已有测试只覆盖了投递环节。这里补上产生与去重两侧的断言。
/// </summary>
[Collection("postgres")]
public class AlertingServiceTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public AlertingServiceTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>首次触发必须真的建出一条 open 告警——这是回归的核心断言。</summary>
    [Fact]
    public async Task 首次触发必须新建告警()
    {
        var key = $"test:{Guid.NewGuid():N}:first";

        await using (var scope = _services.CreateAsyncScope())
        {
            var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
            await alerting.RaiseAsync(key, AlertLevel.Critical, "unit_test", "首次触发", "正文");
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == key);

            Assert.Equal(AlertStatus.Open, alert.Status);
            Assert.Equal(AlertLevel.Critical, alert.Level);
            Assert.Equal("unit_test", alert.Category);
            Assert.Equal("首次触发", alert.Title);
            Assert.Equal(1, alert.OccurrenceCount);
        }
    }

    /// <summary>同键重复触发应累加次数并刷新时间，而不是建出第二条。</summary>
    [Fact]
    public async Task 同键重复触发累加而不新建()
    {
        var key = $"test:{Guid.NewGuid():N}:dedup";

        await using (var scope = _services.CreateAsyncScope())
        {
            var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
            await alerting.RaiseAsync(key, AlertLevel.Warning, "unit_test", "第一次", "正文一");
            await alerting.RaiseAsync(key, AlertLevel.Warning, "unit_test", "第二次", "正文二");
            await alerting.RaiseAsync(key, AlertLevel.Warning, "unit_test", "第三次", "正文三");
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == key);

            Assert.Equal(3, alert.OccurrenceCount);
            // 标题保持首次的，正文更新为最近一次
            Assert.Equal("第一次", alert.Title);
            Assert.Equal("正文三", alert.Message);
        }
    }

    /// <summary>等级升高时应就地提升现有告警的等级（Critical=0，数值小即等级高）。</summary>
    [Fact]
    public async Task 重复触发时等级只升不降()
    {
        var key = $"test:{Guid.NewGuid():N}:level";

        await using (var scope = _services.CreateAsyncScope())
        {
            var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
            await alerting.RaiseAsync(key, AlertLevel.Warning, "unit_test", "先警告");
            await alerting.RaiseAsync(key, AlertLevel.Critical, "unit_test", "后严重");
            await alerting.RaiseAsync(key, AlertLevel.Notice, "unit_test", "再提示");
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == key);
            Assert.Equal(AlertLevel.Critical, alert.Level);
        }
    }

    /// <summary>恢复后同键再次触发，应当建出一条新的 open 告警，而不是复活已恢复的那条。</summary>
    [Fact]
    public async Task 恢复后再次触发新建告警()
    {
        var key = $"test:{Guid.NewGuid():N}:recover";

        await using (var scope = _services.CreateAsyncScope())
        {
            var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
            await alerting.RaiseAsync(key, AlertLevel.Warning, "unit_test", "第一轮");
            await alerting.RecoverAsync(key);
            await alerting.RaiseAsync(key, AlertLevel.Warning, "unit_test", "第二轮");
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alerts = await db.Alerts.AsNoTracking()
                .Where(a => a.AlertKey == key)
                .OrderBy(a => a.FirstOccurredAt)
                .ToListAsync();

            Assert.Equal(2, alerts.Count);
            Assert.Equal(AlertStatus.Recovered, alerts[0].Status);
            Assert.NotNull(alerts[0].RecoveredAt);
            Assert.Equal(AlertStatus.Open, alerts[1].Status);
        }
    }
}
