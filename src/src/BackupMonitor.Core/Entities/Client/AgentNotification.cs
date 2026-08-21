namespace BackupMonitor.Core.Entities.Client;

/// <summary>发给客户端托盘的短消息事件。</summary>
public class AgentNotification
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>alert / backup_completed</summary>
    public string Kind { get; set; } = null!;

    /// <summary>info / warning / critical</summary>
    public string Severity { get; set; } = "info";

    public string Title { get; set; } = null!;

    public string? Message { get; set; }

    /// <summary>用于幂等去重，例如 alert:{id}:opened 或 backup_set:{id}:completed。</summary>
    public string DedupeKey { get; set; } = null!;

    public Guid? AlertId { get; set; }

    public Guid? BackupSetId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>首次由心跳领取并置为已投递的时间；空值表示仍待领取。</summary>
    public DateTime? DeliveredAt { get; set; }

    public Client Client { get; set; } = null!;
}
