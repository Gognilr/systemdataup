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
