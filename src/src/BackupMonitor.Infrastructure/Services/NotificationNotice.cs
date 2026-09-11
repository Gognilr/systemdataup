using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 非告警类通知的落单（日报、备份成功回执）。
///
/// 这类通知和告警共用 notification_deliveries 与 <see cref="NotificationDispatchWorker"/>——
/// 重试、失败告警、渠道凭据解密全都已经在那边，再写一份必然分家。
/// 区别只在于它们自带 Subject/Body，且 AlertId 为空。
///
/// 抽出来是因为日报和备份成功回执在做同一件事，而它们各写一份的时候，
/// 其中一份把 webhook 地址原样写进了 Recipient——那一列会被投递页原样渲染出来，
/// 而 webhook 地址本身就是凭据（含 access_token）。
/// </summary>
public static class NotificationNotice
{
    /// <summary>
    /// webhook 渠道投递记录里的「收件人」。
    ///
    /// 刻意存标签而不是地址：地址是凭据，不该落进一张会被界面原样渲染的表。
    /// 真正发送时用的地址由 <see cref="NotificationDispatchWorker"/> 从解密后的渠道配置里现取。
    /// </summary>
    public const string WecomRecipientLabel = "企业微信群机器人";

    /// <inheritdoc cref="WecomRecipientLabel"/>
    public const string DingtalkRecipientLabel = "钉钉群机器人";

    /// <summary>
    /// 给所有启用的渠道各落一条投递，返回落单条数（调用方负责 SaveChanges）。
    ///
    /// <paramref name="category"/> 非空时按各渠道的「排除类别」过滤：这就是界面上那个
    /// 「另外排除这些类别」能关掉它的入口。为空表示这条通知不可按类别关闭——
    /// 日报走的就是这条路，它要证明的恰恰是「这套系统今天还活着」，
    /// 被关掉之后「一切正常」和「服务端三天前就死了」在收件人那里长得一模一样。
    ///
    /// 刻意**不套用等级筛选**（MinLevel）：那一项管的是告警有多严重，
    /// 而这里的通知不是告警，没有等级可言。硬套的话，默认的 warning 门槛会把
    /// 所有回执一律挡掉，用户打开开关却什么都收不到，然后来问为什么。
    /// </summary>
    public static int Enqueue(
        AppDbContext db,
        NotificationSettingsDto channels,
        string subject,
        string body,
        string? category = null)
    {
        var created = 0;

        if (channels.Email.Enabled && Allows(channels.Email.Filter, category))
        {
            foreach (var recipient in channels.Email.Recipients
                         .Where(r => !string.IsNullOrWhiteSpace(r))
                         .Distinct()
                         .Take(20))
            {
                db.NotificationDeliveries.Add(
                    NewDelivery(NotificationChannel.Email, recipient.Trim(), subject, body));
                created++;
            }
        }

        if (channels.Wecom.Enabled
            && !string.IsNullOrWhiteSpace(channels.Wecom.WebhookUrl)
            && Allows(channels.Wecom.Filter, category))
        {
            db.NotificationDeliveries.Add(
                NewDelivery(NotificationChannel.Wecom, WecomRecipientLabel, subject, body));
            created++;
        }

        if (channels.Dingtalk.Enabled
            && !string.IsNullOrWhiteSpace(channels.Dingtalk.WebhookUrl)
            && Allows(channels.Dingtalk.Filter, category))
        {
            db.NotificationDeliveries.Add(
                NewDelivery(NotificationChannel.Dingtalk, DingtalkRecipientLabel, subject, body));
            created++;
        }

        return created;
    }

    private static bool Allows(NotificationFilterDto? filter, string? category) =>
        category is null
        || filter?.ExcludedCategories is null
        || !filter.ExcludedCategories.Contains(category, StringComparer.OrdinalIgnoreCase);

    private static NotificationDelivery NewDelivery(
        NotificationChannel channel, string recipient, string subject, string body) =>
        new()
        {
            Id = Guid.NewGuid(),
            // AlertId 留空：这不是告警。NotificationDispatchWorker 的
            // 「告警已恢复就别发了」那道守卫对 alert == null 一律放行，正是这里要的。
            AlertId = null,
            Channel = channel,
            Recipient = recipient.Length <= 255 ? recipient : recipient[..255],
            Status = NotificationStatus.Pending,
            AttemptCount = 0,
            Subject = subject,
            Body = body
        };
}
