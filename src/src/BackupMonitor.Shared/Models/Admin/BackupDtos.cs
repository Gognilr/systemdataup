using BackupMonitor.Shared.Models;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>备份列表查询参数（设计书 18.1）</summary>
public class BackupQuery : PagedQuery
{
    public Guid? ClientId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? BusinessUnitId { get; set; }
    public string? ApplicationName { get; set; }

    /// <summary>verifying / available / verification_failed / quarantined / recycle_bin / deleted</summary>
    public string? Status { get; set; }

    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

/// <summary>备份列表项</summary>
public class BackupSetListItemDto
{
    public Guid Id { get; set; }
    public string BackupSetCode { get; set; } = null!;
    public Guid ClientId { get; set; }
    public string ClientHostname { get; set; } = null!;
    public Guid TaskId { get; set; }
    public string TaskName { get; set; } = null!;
    public string ApplicationName { get; set; } = null!;
    public Guid? BusinessUnitId { get; set; }
    public string? BusinessUnitName { get; set; }

    /// <summary>备份版本状态（snake_case）</summary>
    public string Status { get; set; } = null!;

    public DateTime? BackupBusinessTime { get; set; }
    public DateTime UploadedAt { get; set; }
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public bool Locked { get; set; }
    public DateTime? RetentionUntil { get; set; }
}

/// <summary>备份详情（设计书 18.2）</summary>
public class BackupSetDetailDto : BackupSetListItemDto
{
    public Guid SourceCandidateId { get; set; }
    public Guid UploadSessionId { get; set; }
    public DateTime DiscoveredAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? RepositoryPath { get; set; }
    public string? ManifestPath { get; set; }
    public string? ManifestSha256 { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<BackupFileDto> Files { get; set; } = [];
    public List<RetentionLockDto> RetentionLocks { get; set; } = [];
}

/// <summary>备份文件明细（设计书 18.3）</summary>
public class BackupFileDto
{
    public Guid Id { get; set; }
    public string RelativePath { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public long SizeBytes { get; set; }
    public DateTime LastModifiedAt { get; set; }
    public string Sha256 { get; set; } = null!;

    /// <summary>verified / failed</summary>
    public string VerificationStatus { get; set; } = null!;
}

/// <summary>保留锁</summary>
public class RetentionLockDto
{
    public Guid Id { get; set; }
    public string Reason { get; set; } = null!;
    public Guid LockedBy { get; set; }
    public string? LockedByName { get; set; }
    public DateTime LockedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool Active { get; set; }
}

/// <summary>锁定备份请求（设计书 18.4）</summary>
public class LockBackupRequest
{
    public string Reason { get; set; } = null!;

    /// <summary>锁到期时间（null 表示永久）</summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>隔离备份请求（D1）</summary>
public class QuarantineBackupRequest
{
    public string Reason { get; set; } = null!;
}

/// <summary>重新校验响应（设计书 18.6，异步）</summary>
public class VerifyBackupResponse
{
    /// <summary>异步校验操作 ID</summary>
    public Guid OperationId { get; set; }

    /// <summary>verifying</summary>
    public string Status { get; set; } = "verifying";
}
