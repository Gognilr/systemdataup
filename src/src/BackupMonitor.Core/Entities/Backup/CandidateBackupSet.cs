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

    /// <summary>
    /// 大小/文件数可疑（V037 · R15）。
    ///
    /// 可疑**不阻断**：这一份照样收下、照样入库，只是打上标记并发 size_abnormal 告警。
    /// 一份可疑的备份，比没有备份好——判断权交给人，不由系统替人决定这份不要了。
    /// </summary>
    public bool SizeSuspicious { get; set; }

    /// <summary>可疑的具体理由（哪条判据、差多少），写给人看。</summary>
    public string? SizeSuspicionReason { get; set; }

    /// <summary>候选过期时间</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>被哪个新候选替代</summary>
    public Guid? SupersededById { get; set; }

    /// <summary>
    /// 管理员主动取消了这一份的上传（V033）。
    ///
    /// 取消若只落在会话上是不生效的：随后 CommandService 判「这个候选没有 committed、
    /// 也没有 InFlight 会话」→ 认定上传从没落地 → 指令复位重发 →
    /// 建会话时旧会话是 cancelled、幂等键被释放 → 建一个全新会话从 0 重传。
    /// 取消于是变成「几分钟后从头再传一遍」。这个标记是让取消真的停住的那一处状态。
    ///
    /// 清除时机只有两个：源文件变了（清单哈希变化）重新预检，
    /// 或管理员显式再点一次上传——都是「我确实还想要这一份」的明确表示。
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>执行取消的操作人</summary>
    public Guid? CancelledBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // 导航属性
    public Client.Client Client { get; set; } = null!;
    public BackupTask Task { get; set; } = null!;
    public BusinessUnit? BusinessUnit { get; set; }
    public CandidateBackupSet? SupersededBy { get; set; }
    public ICollection<CandidateFile> Files { get; set; } = [];
}
