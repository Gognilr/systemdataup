using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Backup;

/// <summary>正式备份版本（校验入库后生成），对应 backup_sets 表</summary>
public class BackupSet
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public Guid TaskId { get; set; }

    public Guid? BusinessUnitId { get; set; }

    /// <summary>来源候选备份集（一个候选只能产生一个正式版本）</summary>
    public Guid SourceCandidateId { get; set; }

    /// <summary>关联上传会话</summary>
    public Guid UploadSessionId { get; set; }

    /// <summary>
    /// 备份集编码（唯一）。**两种格式并存**：
    /// 2026-08-27 之前入库的是 <c>BS-yyyy-NNNN</c>（如 BS-2026-0001），
    /// 之后是 <c>BS-NNNNNN</c>（如 BS-008932）。
    ///
    /// 序号一直取自全局序列 backup_set_code_seq，跨年不重置——旧格式里的年份
    /// 只是「入库那一年」，并不表示「那一年的第几个」，会误导人（审计 E-16）。
    /// 存量编码不迁移：它们已经写进 manifest.json、审计日志和告警文本，
    /// 改了会破坏可追溯性，而这正是这个编码存在的理由。
    /// 因此任何解析这个字段的代码都必须同时认得两种格式，或者干脆别解析。
    /// </summary>
    public string BackupSetCode { get; set; } = null!;

    public BackupSetStatus Status { get; set; } = BackupSetStatus.Verifying;

    public DateTime? BackupBusinessTime { get; set; }

    public DateTime DiscoveredAt { get; set; }

    public DateTime UploadedAt { get; set; }

    public DateTime? VerifiedAt { get; set; }

    /// <summary>
    /// 正在重新校验的开始时刻（V033）；非空即「校验中」。
    ///
    /// 刻意不借用 <see cref="Status"/> 来表示校验中。借用有两个后果：
    /// 一是原状态丢了——对一个人工隔离的备份点重新校验，哈希对得上就变回可用，
    /// 而当初隔离的理由并不会因为哈希对得上就消失；
    /// 二是校验过程里任何一条提前退出的路径都会把它永久留在 verifying，
    /// 那份备份从此既不可用也删不掉。
    /// </summary>
    public DateTime? VerifyingSince { get; set; }

    /// <summary>仓库存储路径</summary>
    public string? RepositoryPath { get; set; }

    /// <summary>清单文件路径</summary>
    public string? ManifestPath { get; set; }

    /// <summary>清单 SHA-256</summary>
    public string? ManifestSha256 { get; set; }

    public int TotalFiles { get; set; }

    public long TotalBytes { get; set; }

    /// <summary>是否被锁定（禁止清理）</summary>
    public bool Locked { get; set; }

    /// <summary>最早允许清理时间</summary>
    public DateTime? RetentionUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    // 导航属性
    public Client.Client Client { get; set; } = null!;
    public BackupTask Task { get; set; } = null!;
    public BusinessUnit? BusinessUnit { get; set; }
    public CandidateBackupSet SourceCandidate { get; set; } = null!;
    public Upload.UploadSession UploadSession { get; set; } = null!;
    public ICollection<BackupFile> Files { get; set; } = [];
    public ICollection<Retention.RetentionLock> RetentionLocks { get; set; } = [];
}
