using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Alert;

/// <summary>系统告警（支持去重和状态流转），对应 alerts 表。
/// 同一 alert_key 在活动状态（open/acknowledged/in_progress）下只能有一条，由部分唯一索引保证。</summary>
public class Alert
{
    public Guid Id { get; set; }

    /// <summary>告警去重键（如 task:{taskId}:upload_overdue）</summary>
    public string AlertKey { get; set; } = null!;

    public AlertLevel Level { get; set; }

    public AlertStatus Status { get; set; } = AlertStatus.Open;

    /// <summary>告警分类（如 backup_delay、client_offline）</summary>
    public string? Category { get; set; }

    public Guid? ClientId { get; set; }

    public Guid? TaskId { get; set; }

    public Guid? BusinessUnitId { get; set; }

    public Guid? BackupSetId { get; set; }

    public string Title { get; set; } = null!;

    public string? Message { get; set; }

    /// <summary>首次发生时间</summary>
    public DateTime FirstOccurredAt { get; set; }

    /// <summary>最近发生时间（去重聚合时更新）</summary>
    public DateTime LastOccurredAt { get; set; }

    /// <summary>累计发生次数</summary>
    public int OccurrenceCount { get; set; } = 1;

    /// <summary>
    /// 最近一次为这条告警生成通知投递的时间（V033）。
    ///
    /// 告警此前只有两个通知触发点：新建、等级提升。一条 Critical 挂在那里三天没人处理，
    /// 系统只在第一分钟发过一封邮件——之后完全沉默，而「没有新邮件」在收件人那里
    /// 读起来和「已经好了」是一样的。距今超过 alert_renotify_hours 就重发一次。
    ///
    /// 空值表示从未通知过（命中静默规则，或建告警时渠道尚未配置）。
    /// </summary>
    public DateTime? LastNotifiedAt { get; set; }

    /// <summary>确认人</summary>
    public Guid? AcknowledgedBy { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>指派给（功能说明书 8.18）</summary>
    public Guid? AssignedTo { get; set; }

    public DateTime? AssignedAt { get; set; }

    /// <summary>自动恢复时间</summary>
    public DateTime? RecoveredAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    /// <summary>处理备注</summary>
    public string? HandlingNote { get; set; }

    /// <summary>附加元数据（jsonb）</summary>
    public string? Metadata { get; set; }

    // 导航属性
    public Client.Client? Client { get; set; }
    public Backup.BackupTask? Task { get; set; }
    public Backup.BusinessUnit? BusinessUnit { get; set; }
    public Backup.BackupSet? BackupSet { get; set; }
    public Rbac.User? AcknowledgedByUser { get; set; }
    public Rbac.User? AssignedToUser { get; set; }
    public ICollection<NotificationDelivery> Deliveries { get; set; } = [];
}
