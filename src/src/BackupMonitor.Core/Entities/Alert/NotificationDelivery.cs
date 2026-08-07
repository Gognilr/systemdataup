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

    // 导航属性
    public Alert? Alert { get; set; }
}
