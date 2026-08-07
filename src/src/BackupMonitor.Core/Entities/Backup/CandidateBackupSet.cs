using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Backup;

/// <summary>候选备份集（预检阶段产生），对应 candidate_backup_sets 表</summary>
public class CandidateBackupSet
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public Guid TaskId { get; set; }

    /// <summary>所属业务单元（subdirectory_units 类型任务才有）</summary>
    public Guid? BusinessUnitId { get; set; }

    /// <summary>候选键（用于去重识别）</summary>
    public string CandidateKey { get; set; } = null!;

    /// <summary>候选集根路径</summary>
    public string SourceRoot { get; set; } = null!;

    /// <summary>备份业务时间（如账套备份对应的业务日期）</summary>
    public DateTime? BackupBusinessTime { get; set; }

    /// <summary>发现时间</summary>
    public DateTime DiscoveredAt { get; set; }

    public DateTime? PrecheckedAt { get; set; }

    public PrecheckStatus PrecheckStatus { get; set; } = PrecheckStatus.NotScanned;

    public int? TotalFiles { get; set; }

    public long? TotalBytes { get; set; }

    /// <summary>清单哈希（SHA-256）</summary>
    public string? ManifestHash { get; set; }

    /// <summary>快速指纹（用于增量判断）</summary>
    public string? QuickFingerprint { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>候选过期时间</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>被哪个新候选替代</summary>
    public Guid? SupersededById { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // 导航属性
    public Client.Client Client { get; set; } = null!;
    public BackupTask Task { get; set; } = null!;
    public BusinessUnit? BusinessUnit { get; set; }
    public CandidateBackupSet? SupersededBy { get; set; }
    public ICollection<CandidateFile> Files { get; set; } = [];
}
