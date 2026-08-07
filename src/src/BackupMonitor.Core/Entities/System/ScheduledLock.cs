namespace BackupMonitor.Core.Entities.System;

/// <summary>定时任务单实例锁（设计书 §25"单实例锁"），对应 scheduled_locks 表。
/// 主键为 lock_key（varchar），非 UUID；TTL 到期后允许其他实例抢占（崩溃自动恢复）。</summary>
public class ScheduledLock
{
    /// <summary>任务锁键（主键，如 worker:notification_dispatch）</summary>
    public string LockKey { get; set; } = null!;

    /// <summary>持锁实例标识（机器名:进程号:作用域GUID）</summary>
    public string OwnerId { get; set; } = null!;

    /// <summary>获取锁时间</summary>
    public DateTime AcquiredAt { get; set; }

    /// <summary>锁到期时间，到期后允许其他实例抢占</summary>
    public DateTime ExpiresAt { get; set; }
}
