namespace BackupMonitor.Core.Entities.Retention;

/// <summary>备份版本保留锁（阻止清理），对应 retention_locks 表</summary>
public class RetentionLock
{
    public Guid Id { get; set; }

    public Guid BackupSetId { get; set; }

    /// <summary>锁定原因（如审计要求、诉讼保全）</summary>
    public string Reason { get; set; } = null!;

    /// <summary>锁定操作人</summary>
    public Guid LockedBy { get; set; }

    public DateTime LockedAt { get; set; }

    /// <summary>锁到期时间（null 表示永久）</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>是否生效中</summary>
    public bool Active { get; set; } = true;

    // 导航属性
    public Backup.BackupSet BackupSet { get; set; } = null!;
    public Rbac.User LockedByUser { get; set; } = null!;
}
