namespace BackupMonitor.Core.Entities.Alert;

/// <summary>
/// 告警临时静默（功能说明书 8.18）：命中期间新告警只累加计数、不发通知、不推送 Agent 托盘。
/// <see cref="AlertKeyPattern"/> 用 SQL LIKE 语法（% 通配），如 client:{clientId}:% 覆盖某客户端全部告警键。
/// </summary>
public class AlertSilence
{
    public Guid Id { get; set; }

    public string AlertKeyPattern { get; set; } = null!;

    public string Reason { get; set; } = null!;

    /// <summary>静默截止时间</summary>
    public DateTime Until { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    // 导航属性
    public Rbac.User? CreatedByUser { get; set; }
}
