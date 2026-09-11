using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Alert;

/// <summary>通知发送记录，对应 notification_deliveries 表</summary>
public class NotificationDelivery
{
    public Guid Id { get; set; }

    /// <summary>关联告警（告警删除后置空）</summary>
    public Guid? AlertId { get; set; }

    public NotificationChannel Channel { get; set; }

    /// <summary>接收人标识（邮箱/企业微信账号等）</summary>
    public string Recipient { get; set; } = null!;

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    public int AttemptCount { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    public DateTime? SentAt { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 标题前缀（审计 G-11）。等级提升补发的那一封带「【已升级】」，
    /// 让收件人一眼看出这不是同一封警告的重发。
    ///
    /// 放在投递记录而不是改 alerts.title：告警标题描述的是「出了什么事」，
    /// 那件事本身没变，变的只是这一次通知要传达的信息；
    /// 而且同一条告警可能升级多次，改标题会让前缀越叠越长。
    /// </summary>
    public string? TitlePrefix { get; set; }

    /// <summary>
    /// 投递自带标题。为空时按关联告警组装（V038 · R8）。
    ///
    /// 日报复用这条投递通道但没有对应的告警行——不带上内容的话，
    /// 发出去的是一封标题「系统告警」、正文空白的邮件。
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>投递自带正文。为空时按关联告警组装。</summary>
    public string? Body { get; set; }

    // 导航属性
    public Alert? Alert { get; set; }
}
