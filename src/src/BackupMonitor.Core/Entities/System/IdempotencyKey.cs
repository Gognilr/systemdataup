namespace BackupMonitor.Core.Entities.System;

/// <summary>通用幂等键（设计书 8.5），对应 idempotency_keys 表。
/// 复合主键 (scope, idempotency_key)；重放请求返回首次创建的资源 Id。</summary>
public class IdempotencyKey
{
    /// <summary>业务作用域（复合主键之一，如 restore.create）</summary>
    public string Scope { get; set; } = null!;

    /// <summary>客户端提供的 Idempotency-Key（复合主键之一）</summary>
    public string Key { get; set; } = null!;

    /// <summary>首次请求创建的资源 Id（重放时原样返回）</summary>
    public Guid ResourceId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>幂等有效期，过期后同键视为新请求</summary>
    public DateTime ExpiresAt { get; set; }
}
