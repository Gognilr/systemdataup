namespace BackupMonitor.Core.Entities.Backup;

/// <summary>候选文件（预检清单明细），对应 candidate_files 表</summary>
public class CandidateFile
{
    public Guid Id { get; set; }

    public Guid CandidateBackupSetId { get; set; }

    /// <summary>相对候选集根路径</summary>
    public string RelativePath { get; set; } = null!;

    public string FileName { get; set; } = null!;

    public long SizeBytes { get; set; }

    public DateTime LastModifiedAt { get; set; }

    /// <summary>全量 SHA-256（可能延迟计算）</summary>
    public string? Sha256 { get; set; }

    /// <summary>快速哈希（头部采样）</summary>
    public string? QuickHash { get; set; }

    /// <summary>是否为必需文件（缺失即预检失败）</summary>
    public bool IsRequired { get; set; }

    public int SortOrder { get; set; }

    // 导航属性
    public CandidateBackupSet CandidateBackupSet { get; set; } = null!;
}
