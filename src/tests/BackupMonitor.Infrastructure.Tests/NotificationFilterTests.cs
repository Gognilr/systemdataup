using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 按渠道筛选告警投递（整改清单 2026-09-11 · R24）。
///
/// 在此之前只有渠道总开关：启用邮件 = 20 多类告警每一条都发。
/// 「客户端 CPU 吃紧」「客户端版本落后」这类最容易变成没人看的背景噪音，
/// 而它们一旦被习惯性忽略，「备份复查未通过」也会跟着被忽略——
/// 报警的可信度是整体的，摊薄了就都没了。
///
/// 这套筛选敢于默认挡掉 Notice，前提是它筛的是**投递**不是**告警**：
/// 被挡下的告警照样建、照样计数、照样出现在管理网页和客户端托盘上。
/// </summary>
public class NotificationFilterTests
{
    [Fact]
    public void 没有配置时默认发严重和警告()
    {
        // 老配置里根本没有 filter 这个字段，反序列化出来就是一个空对象。
        var filter = new NotificationFilterDto();

        Assert.True(AlertingService.PassesFilter(filter, Alert(AlertLevel.Critical)));
        Assert.True(AlertingService.PassesFilter(filter, Alert(AlertLevel.Warning)));
        Assert.False(AlertingService.PassesFilter(filter, Alert(AlertLevel.Notice)));
    }

    [Fact]
    public void filter整个为null也按默认处理()
    {
        Assert.True(AlertingService.PassesFilter(null, Alert(AlertLevel.Warning)));
        Assert.False(AlertingService.PassesFilter(null, Alert(AlertLevel.Notice)));
    }

    [Fact]
    public void 只发严重时警告被挡下()
    {
        var filter = new NotificationFilterDto { MinLevel = "critical" };

        Assert.True(AlertingService.PassesFilter(filter, Alert(AlertLevel.Critical)));
        Assert.False(AlertingService.PassesFilter(filter, Alert(AlertLevel.Warning)));
        Assert.False(AlertingService.PassesFilter(filter, Alert(AlertLevel.Notice)));
    }

    [Fact]
    public void 全发时提示级也发()
    {
        var filter = new NotificationFilterDto { MinLevel = "notice" };

        Assert.True(AlertingService.PassesFilter(filter, Alert(AlertLevel.Notice)));
    }

    [Fact]
    public void 排除的类别即使等级够也不发()
    {
        var filter = new NotificationFilterDto
        {
            MinLevel = "warning",
            ExcludedCategories = ["client_resource", "agent_version_drift"]
        };

        Assert.False(AlertingService.PassesFilter(filter, Alert(AlertLevel.Warning, "client_resource")));
        // 排除是按类别，不看等级：同一类别升到严重照样不发。
        Assert.False(AlertingService.PassesFilter(filter, Alert(AlertLevel.Critical, "client_resource")));
        // 没被排除的类别不受影响
        Assert.True(AlertingService.PassesFilter(filter, Alert(AlertLevel.Warning, "backup_missed")));
    }

    [Fact]
    public void 等级名写错时退回默认而不是放行一切()
    {
        // 直接改库、或者旧版本写进来的值。宁可按默认（严重+警告）处理，
        // 也不能因为一个认不出的字符串就把 Notice 全放出去或者把 Critical 全挡掉。
        var filter = new NotificationFilterDto { MinLevel = "urgent" };

        Assert.True(AlertingService.PassesFilter(filter, Alert(AlertLevel.Critical)));
        Assert.True(AlertingService.PassesFilter(filter, Alert(AlertLevel.Warning)));
        Assert.False(AlertingService.PassesFilter(filter, Alert(AlertLevel.Notice)));
    }

    [Fact]
    public void 类别比对不分大小写也不受空格影响()
    {
        var filter = new NotificationFilterDto { ExcludedCategories = [" Client_Resource "] };

        Assert.False(AlertingService.PassesFilter(filter, Alert(AlertLevel.Warning, "client_resource")));
    }

    /// <summary>界面上勾得到的类别，必须覆盖代码里真的会 Raise 出来的那些。</summary>
    [Theory]
    [InlineData("backup_missed")]
    [InlineData("client_resource")]
    [InlineData("verification_failed")]
    [InlineData("agent_version_drift")]
    [InlineData("notification_channel_failed")]
    [InlineData("client_ca_expiring")]
    public void 类别清单覆盖了实际会产生的告警(string category)
    {
        Assert.Contains(AlertCategoryCatalog.All, c => c.Key == category);
    }

    private static Alert Alert(AlertLevel level, string category = "backup_missed") =>
        new() { Id = Guid.NewGuid(), Level = level, Category = category, Title = "测试" };
}
