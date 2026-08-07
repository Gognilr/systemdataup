using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Backup;

/// <summary>正式备份文件明细，对应 backup_files 表</summary>
public class BackupFile
{
    public Guid Id { get; set; }

    public Guid BackupSetId { get; set; }

    /// <summary>原始相对路径</summary>
    public string RelativePath { get; set; } = null!;

    public string FileName { get; set; } = null!;

    public long SizeBytes { get; set; }

    public DateTime LastModifiedAt { get; set; }

    /// <summary>文件 SHA-256（双端校验依据）</summary>
    public string Sha256 { get; set; } = null!;

    /// <summary>仓库内相对路径</summary>
    public string RepositoryRelativePath { get; set; } = null!;

    public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.Verified;

    public DateTime CreatedAt { get; set; }

    // 导航属性
    public BackupSet BackupSet { get; set; } = null!;
}
