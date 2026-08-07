using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Backup;

/// <summary>服务端下发给客户端的指令队列，对应 commands 表</summary>
public class Command
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>关联任务（可空）</summary>
    public Guid? TaskId { get; set; }

    /// <summary>关联候选备份集（可空）</summary>
    public Guid? CandidateBackupSetId { get; set; }

    public CommandType CommandType { get; set; }

    public CommandStatus Status { get; set; } = CommandStatus.Pending;

    /// <summary>执行优先级（越小越优先）</summary>
    public int Priority { get; set; } = 100;

    /// <summary>指令参数（jsonb）</summary>
    public string? Payload { get; set; }

    /// <summary>一次性随机数（防重放，唯一）</summary>
    public string Nonce { get; set; } = null!;

    /// <summary>服务端签名（客户端校验指令来源）</summary>
    public string? Signature { get; set; }

    /// <summary>下发人</summary>
    public Guid? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>指令过期时间（过期未认领则作废）</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>客户端认领时间</summary>
    public DateTime? ClaimedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ResultCode { get; set; }

    public string? ResultMessage { get; set; }

    /// <summary>执行结果数据（jsonb）</summary>
    public string? ResultPayload { get; set; }

    /// <summary>幂等键（唯一）</summary>
    public string? IdempotencyKey { get; set; }

    // 导航属性
    public Client.Client Client { get; set; } = null!;
    public BackupTask? Task { get; set; }
    public CandidateBackupSet? CandidateBackupSet { get; set; }
    public Rbac.User? CreatedByUser { get; set; }
}
