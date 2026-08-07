namespace BackupMonitor.Core.Entities.Rbac;

/// <summary>
/// 管理员刷新令牌（只存哈希，可吊销，支持轮换），对应 refresh_tokens 表。
/// 由 V002__refresh_tokens_and_batch_idempotency.sql 迁移引入，
/// 满足设计书 9.3（退出吊销）与 23.2（Refresh Token 可撤销）要求。
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>令牌 SHA-256 哈希（明文令牌仅登录/刷新时一次性返回）</summary>
    public string TokenHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>吊销原因（logout/rotated/expired_purge/admin_revoke）</summary>
    public string? RevokeReason { get; set; }

    /// <summary>轮换时被新令牌替代（指向新令牌哈希）</summary>
    public string? ReplacedByHash { get; set; }

    public string? CreatedIp { get; set; }

    public string? UserAgent { get; set; }

    // 导航属性
    public User User { get; set; } = null!;
}
