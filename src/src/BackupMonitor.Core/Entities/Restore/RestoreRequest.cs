using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Restore;

/// <summary>恢复下载请求记录，对应 restore_requests 表</summary>
public class RestoreRequest
{
    public Guid Id { get; set; }

    public Guid BackupSetId { get; set; }

    /// <summary>申请人</summary>
    public Guid RequestedBy { get; set; }

    /// <summary>恢复用途说明（审计必填）</summary>
    public string Purpose { get; set; } = null!;

    public RestoreRequestStatus Status { get; set; } = RestoreRequestStatus.Requested;

    public DateTime RequestedAt { get; set; }

    /// <summary>审批通过时间</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>下载令牌哈希（明文令牌仅一次性返回）</summary>
    public string? DownloadTokenHash { get; set; }

    /// <summary>下载令牌过期时间</summary>
    public DateTime? DownloadExpiresAt { get; set; }

    public long DownloadedBytes { get; set; }

    /// <summary>已完整交付的文件相对路径（jsonb 数组，V012；审查 P1-7 完成判定依据）</summary>
    public string DeliveredPaths { get; set; } = "[]";

    public DateTime? CompletedAt { get; set; }

    /// <summary>下载方 IP</summary>
    public string? ClientIp { get; set; }

    public string? ErrorMessage { get; set; }

    // 导航属性
    public Backup.BackupSet BackupSet { get; set; } = null!;
    public Rbac.User RequestedByUser { get; set; } = null!;
}
