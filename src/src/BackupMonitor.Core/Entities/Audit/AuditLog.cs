using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Audit;

/// <summary>审计日志（只追加，不可修改删除），对应 audit_logs 表（按月范围分区）。
/// 复合主键 (id, occurred_at) 以支持分区表要求（分区键必须包含在主键中）。</summary>
public class AuditLog
{
    public Guid Id { get; set; }

    /// <summary>事件发生时间（分区键）</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>操作人（系统动作为空）</summary>
    public Guid? UserId { get; set; }

    /// <summary>操作时用户名快照</summary>
    public string? UsernameSnapshot { get; set; }

    public string? ClientIp { get; set; }

    /// <summary>操作动作（如 task.create、upload.commit）</summary>
    public string Action { get; set; } = null!;

    /// <summary>资源类型（如 backup_task、client）</summary>
    public string? ResourceType { get; set; }

    public Guid? ResourceId { get; set; }

    /// <summary>请求追踪 ID</summary>
    public string? RequestId { get; set; }

    public AuditResult Result { get; set; }

    /// <summary>变更前数据（jsonb）</summary>
    public string? BeforeData { get; set; }

    /// <summary>变更后数据（jsonb）</summary>
    public string? AfterData { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>附加元数据（jsonb）</summary>
    public string? Metadata { get; set; }
}
