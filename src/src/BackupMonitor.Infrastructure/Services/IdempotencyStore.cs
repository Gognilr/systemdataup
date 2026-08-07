using BackupMonitor.Core.Entities.System;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 通用幂等键存储（设计书 8.5）。把 Idempotency-Key 从进程内存升级为数据库持久化：
/// 重启不丢、多实例共享。典型用法（以恢复创建为例）：
///   1) 快路径：FindAsync 命中未过期键 → 直接返回首次结果；
///   2) 写路径：Track 把幂等键与业务实体挂进同一 DbContext，随业务 SaveChanges 原子提交；
///   3) 并发兜底：两个相同键并发到达时，后提交者触发 (scope, idempotency_key) 唯一冲突，
///      整个事务回滚（业务实体也不会落库），用 IsDuplicateKeyException 识别后回首查结果。
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>查找未过期的幂等键，返回首次创建的资源 Id；不存在返回 null（顺带清理过期行）。</summary>
    Task<Guid?> FindAsync(string scope, string idempotencyKey, CancellationToken ct = default);

    /// <summary>把幂等键登记到当前 DbContext（不立即提交，由调用方与业务实体同事务 SaveChanges）。</summary>
    void Track(string scope, string idempotencyKey, Guid resourceId, TimeSpan ttl);

    /// <summary>判断异常是否为幂等键重复冲突（PostgreSQL 23505 且命中 idempotency_keys 主键约束）。</summary>
    bool IsDuplicateKeyException(Exception ex);
}

public class IdempotencyStore : IIdempotencyStore
{
    private readonly AppDbContext _db;

    public IdempotencyStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Guid?> FindAsync(string scope, string idempotencyKey, CancellationToken ct = default)
    {
        // 顺手清理过期键（表小、expires_at 有索引，代价可忽略）
        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM idempotency_keys WHERE expires_at <= now()", ct);

        return await _db.IdempotencyKeys.AsNoTracking()
            .Where(k => k.Scope == scope && k.Key == idempotencyKey && k.ExpiresAt > DateTime.UtcNow)
            .Select(k => (Guid?)k.ResourceId)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public void Track(string scope, string idempotencyKey, Guid resourceId, TimeSpan ttl)
    {
        _db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Scope = scope,
            Key = idempotencyKey,
            ResourceId = resourceId,
            ExpiresAt = DateTime.UtcNow.Add(ttl)
        });
    }

    /// <inheritdoc />
    public bool IsDuplicateKeyException(Exception ex) =>
        ex is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            } pg
        }
        && pg.ConstraintName is not null
        && pg.ConstraintName.Contains("idempotency_keys", StringComparison.Ordinal);
}
