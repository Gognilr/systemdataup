using System.ComponentModel.DataAnnotations;
using BackupMonitor.Shared.Models;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>告警列表查询参数（设计书 20.1）</summary>
public class AlertQuery : PagedQuery
{
    /// <summary>critical / warning / notice</summary>
    public string? Level { get; set; }

    /// <summary>open / acknowledged / in_progress / recovered / closed / ignored</summary>
    public string? Status { get; set; }

    public string? Category { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? TaskId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>只看指派给该用户的告警（"指派给我"筛选传当前用户 id）</summary>
    public Guid? AssignedTo { get; set; }
}

/// <summary>告警列表项</summary>
public class AlertListItemDto
{
    public Guid Id { get; set; }
    public string AlertKey { get; set; } = null!;

    /// <summary>critical / warning / notice</summary>
    public string Level { get; set; } = null!;

    /// <summary>open / acknowledged / in_progress / recovered / closed / ignored</summary>
    public string Status { get; set; } = null!;

    public string? Category { get; set; }
    public Guid? ClientId { get; set; }
    public string? ClientHostname { get; set; }
    public Guid? TaskId { get; set; }
    public string? TaskName { get; set; }
    public string Title { get; set; } = null!;
    public string? Message { get; set; }
    public DateTime FirstOccurredAt { get; set; }
    public DateTime LastOccurredAt { get; set; }
    public int OccurrenceCount { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    public Guid? AssignedTo { get; set; }
    public string? AssignedToName { get; set; }
    public DateTime? AssignedAt { get; set; }

    /// <summary>命中一条未到期的静默规则</summary>
    public bool IsSilenced { get; set; }
}

/// <summary>告警详情</summary>
public class AlertDetailDto : AlertListItemDto
{
    public Guid? BusinessUnitId { get; set; }
    public string? BusinessUnitName { get; set; }
    public Guid? BackupSetId { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public string? AcknowledgedByName { get; set; }
    public DateTime? RecoveredAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? HandlingNote { get; set; }
    public string? Metadata { get; set; }
}

/// <summary>更新告警处理状态请求（设计书 20.3）</summary>
public class HandleAlertRequest
{
    /// <summary>in_progress / ignored</summary>
    [Required]
    public string Status { get; set; } = null!;

    public string? Note { get; set; }
}

/// <summary>关闭告警请求（设计书 20.4）</summary>
public class CloseAlertRequest
{
    public string? Note { get; set; }
}

/// <summary>指派告警请求（功能说明书 8.18）。AssignedTo 传 null 表示取消指派。</summary>
public class AssignAlertRequest
{
    public Guid? AssignedTo { get; set; }
}

/// <summary>
/// 创建临时静默请求。范围默认按客户端（该客户端后续全部告警键，
/// 覆盖维护窗口场景）；没有客户端时退化为按这条告警自身的 alert_key 精确静默。
/// </summary>
public class SilenceAlertRequest
{
    /// <summary>静默时长（小时），1~720（30 天）</summary>
    [Range(1, 720)]
    public int Hours { get; set; }

    public string? Reason { get; set; }
}

/// <summary>告警静默项</summary>
public class AlertSilenceDto
{
    public Guid Id { get; set; }
    public string AlertKeyPattern { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public DateTime Until { get; set; }
    public Guid CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>审计日志查询参数（设计书 21.1）</summary>
public class AuditLogQuery : PagedQuery
{
    public Guid? UserId { get; set; }
    public string? Action { get; set; }
    public string? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }

    /// <summary>success / failure</summary>
    public string? Result { get; set; }

    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? RequestId { get; set; }
}

/// <summary>审计日志项</summary>
public class AuditLogDto
{
    public Guid Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public Guid? UserId { get; set; }
    public string? UsernameSnapshot { get; set; }
    public string? ClientIp { get; set; }
    public string Action { get; set; } = null!;
    public string? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }
    public string? RequestId { get; set; }

    /// <summary>success / failure</summary>
    public string Result { get; set; } = null!;

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? BeforeData { get; set; }
    public string? AfterData { get; set; }
}
