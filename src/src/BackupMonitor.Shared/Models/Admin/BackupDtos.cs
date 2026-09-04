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

/// <summary>
/// 备份集按「客户端 + 任务」归拢后的一行。
///
/// 平铺的列表在多业务单元任务上是不能用的：U8 一个任务 18 个账套，一天就是 18 行，
/// 一周 126 行，中间夹着别的任务——「这个任务今天备齐了没有」这个问题
/// 要靠人一行行数才能回答，而它恰恰是这张表存在的唯一理由。
///
/// 所以归拢之后每行必须自带结论：几个账套、最新一份是什么时候、有没有不可用的。
/// </summary>
public class BackupSetGroupDto
{
    public Guid ClientId { get; set; }
    public string ClientHostname { get; set; } = null!;
    public Guid TaskId { get; set; }
    public string TaskName { get; set; } = null!;
    public string ApplicationName { get; set; } = null!;

    /// <summary>这一组里的备份集数量（受当前筛选影响）</summary>
    public int BackupSetCount { get; set; }

    /// <summary>
    /// 这一组里出现过几个不同的业务单元。U8 任务这里就是账套数——
    /// 它是「18 个账套备齐了没有」唯一能对照的那个数。
    /// 单单元任务（没有业务单元）算作 1。
    /// </summary>
    public int BusinessUnitCount { get; set; }

    /// <summary>最新一份备份的业务时间。这一列决定「这个任务是不是还在正常出备份」。</summary>
    public DateTime? LatestBusinessTime { get; set; }

    /// <summary>最新一份的入库时间。业务时间可能为空（识别不出来），这时靠它兜底。</summary>
    public DateTime? LatestUploadedAt { get; set; }

    public long TotalBytes { get; set; }

    /// <summary>状态为「可用」的份数</summary>
    public int AvailableCount { get; set; }

    /// <summary>
    /// 不可用的份数（校验失败 / 隔离 / 回收站 / 已删除 / 校验中）。
    /// 折叠之后这个数是唯一的警报——它不为 0 时这一组必须一眼看得出来。
    /// </summary>
    public int NotAvailableCount { get; set; }

    public int LockedCount { get; set; }
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

    /// <summary>
    /// 非空表示这一份正在重新校验。校验刻意不改 Status（改了会丢掉原状态，
    /// 隔离尤其不能丢），所以「校验中」这件事要靠这个字段单独告诉界面。
    /// </summary>
    public DateTime? VerifyingSince { get; set; }

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

/// <summary>
/// 批量回收站操作请求（删除 / 还原 / 彻底删除共用）。
/// 一次带上要处理的备份集 id，服务端逐条执行——一条做不了不该拖累其余几十条。
/// </summary>
public class BackupSetBatchRequest
{
    public List<Guid> BackupSetIds { get; set; } = new();
}

/// <summary>批量操作里失败的那一条，以及为什么</summary>
public class BackupSetBatchFailure
{
    public Guid BackupSetId { get; set; }

    /// <summary>备份集编号。只有 id 的话，界面上报出来的失败人是对不上号的。</summary>
    public string? BackupSetCode { get; set; }

    public string ErrorCode { get; set; } = "ERROR";

    public string Message { get; set; } = null!;
}

/// <summary>
/// 批量操作结果。成功几条、失败哪几条各自因为什么——
/// 闷声跳过等于让人以为整批都做完了。
/// </summary>
public class BackupSetBatchResult
{
    public int SuccessCount { get; set; }

    public List<BackupSetBatchFailure> Failed { get; set; } = new();
}
