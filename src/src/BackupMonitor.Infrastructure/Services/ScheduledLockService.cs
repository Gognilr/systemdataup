using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 定时任务单实例锁服务（设计书 §25"单实例锁"要求）。
/// 多实例部署时保证同一时刻只有一个实例执行每类定时任务：
/// 获取：INSERT ... ON CONFLICT DO UPDATE ... WHERE expires_at &lt;= now()（单条语句原子完成，
///       行不存在→插入得锁；已存在但过期→抢占得锁；未过期→无行返回即失败）；
/// 释放：任务结束 DELETE 自己持有的行（owner_id 匹配，绝不误删他人的锁）；
/// 崩溃恢复：进程崩溃未释放时，锁在 TTL 到期后自动可被其他实例抢占。
/// TTL 应大于任务单轮最坏耗时，避免执行中被抢占导致重复执行。
/// 作用域服务：每个 DI 作用域（后台任务每轮一个 scope）生成唯一 owner 标识。
/// </summary>
public interface IScheduledLockService
{
    /// <summary>尝试获取指定任务的单实例锁；已被其他实例持有时返回 false（不阻塞）。</summary>
    Task<bool> TryAcquireAsync(string lockKey, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>释放本实例持有的锁（幂等；锁不存在或已被抢占时静默返回）。</summary>
    Task ReleaseAsync(string lockKey, CancellationToken ct = default);
}

public class ScheduledLockService : IScheduledLockService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ScheduledLockService> _logger;

    /// <summary>本作用域的持锁者标识（机器名:进程号:作用域GUID），释放时用于归属校验</summary>
    private readonly string _ownerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public ScheduledLockService(AppDbContext db, ILogger<ScheduledLockService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string lockKey, TimeSpan ttl, CancellationToken ct = default)
    {
        // TimeSpan 参数经 Npgsql 映射为 PostgreSQL interval，expires_at 全部取数据库时钟，避免多机时钟偏差。
        var rows = await _db.ScheduledLocks
            .FromSqlRaw(
                @"INSERT INTO scheduled_locks (lock_key, owner_id, acquired_at, expires_at)
                  VALUES ({0}, {1}, now(), now() + {2})
                  ON CONFLICT (lock_key) DO UPDATE
                  SET owner_id = EXCLUDED.owner_id,
                      acquired_at = EXCLUDED.acquired_at,
                      expires_at = EXCLUDED.expires_at
                  WHERE scheduled_locks.expires_at <= now()
                  RETURNING lock_key, owner_id, acquired_at, expires_at",
                lockKey, _ownerId, ttl)
            .AsNoTracking()
            .ToListAsync(ct);

        var acquired = rows.Count == 1;
        if (!acquired)
            _logger.LogDebug("定时任务锁 {LockKey} 由其他实例持有，本轮跳过", lockKey);

        return acquired;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string lockKey, CancellationToken ct = default)
    {
        // 仅删除自己持有的行；若锁已过期被抢占，则不会误删新持有者的锁
        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM scheduled_locks WHERE lock_key = {0} AND owner_id = {1}",
            new object[] { lockKey, _ownerId }, ct);
    }
}
