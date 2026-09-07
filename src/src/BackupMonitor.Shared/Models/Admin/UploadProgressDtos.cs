namespace BackupMonitor.Shared.Models.Admin;

/// <summary>
/// 正在传输中的上传会话（管理端可见的进度）。
///
/// 这些数字本来就一直在库里——UploadSessionService 每收一个分块就重算一次
/// uploaded_bytes 并刷新 last_activity_at——但此前只通过 Agent 端接口回给客户端自己（断点续传要用）。
/// 管理端能看到的只有一个「上传中」徽标，于是传一份几十 GB 的备份，
/// 界面上从头到尾没有任何数字在动，人无法区分「在传」和「卡死了」。
/// </summary>
public class UploadProgressDto
{
    public Guid SessionId { get; set; }

    public Guid ClientId { get; set; }

    public string Hostname { get; set; } = string.Empty;

    public string ClientDisplayName { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public string TaskName { get; set; } = string.Empty;

    /// <summary>
    /// 这次传的是哪个业务单元（账套）。一个上传会话只对应一个候选备份集，
    /// 也就是一个账套——U8 一台机器 18 个账套时，只显示任务名的话这 18 条长得一模一样，
    /// 人看不出「现在备到哪个账套了」。老数据里候选可能没有业务单元，此时为 null。
    /// </summary>
    public string? BusinessUnitName { get; set; }

    public string Status { get; set; } = string.Empty;

    public int TotalFiles { get; set; }

    public long TotalBytes { get; set; }

    public long UploadedBytes { get; set; }

    /// <summary>已传百分比（0–100，两位小数）。total_bytes 为 0 时给 0 而不是除零。</summary>
    public decimal Percent { get; set; }

    /// <summary>
    /// 当前速度（字节/秒）。取两次观测之间的增量，不是全程平均——
    /// 全程平均在传输变慢或卡住时不会跟着掉，那正是最需要看出问题的时刻。
    /// 首次观测还没有可比的上一次，此时退回全程平均；两者都算不出来时为 null。
    /// </summary>
    public long? BytesPerSecond { get; set; }

    /// <summary>
    /// 最近若干次采到的瞬时速率（字节/秒，旧→新），用来在界面上画一条一分钟的走势线。
    ///
    /// 存在的理由：一个瞬时数字只答得出「现在多快」，答不出「在变快还是在变慢」，
    /// 而运维真正要分的是三种状态——链路正常、正在退化、已经卡住。
    /// 第三种由 <see cref="Stalled"/> 判，第二种此前无处可读。
    ///
    /// 采样点不足两个时为空数组，**不补零**：补零会画出一条从 0 冲上来的假曲线，
    /// 让刚开始的传输看起来像刚刚提速。
    /// </summary>
    public IReadOnlyList<long> RecentRates { get; set; } = [];

    /// <summary>预计剩余秒数。速度未知或为 0 时为 null——宁可不显示，也不要显示一个假的「剩余 0 秒」。</summary>
    public long? EtaSeconds { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? LastActivityAt { get; set; }

    /// <summary>距最后一次收到数据的秒数。这是判断「慢」和「死」的唯一依据。</summary>
    public long IdleSeconds { get; set; }

    /// <summary>
    /// 是否疑似卡住。阈值随实测速度放大（见 UploadProgressService），
    /// 固定阈值会把窄带上正常传一个大分块误判成卡死。
    /// </summary>
    public bool Stalled { get; set; }
}

/// <summary>
/// 全局上传闸的当前状态（D3）。限流如果在界面上看不见，它的表现就是「点了没反应」，
/// 那比不限流更糟——人会以为系统坏了，然后去点更多次。
/// </summary>
public class UploadQueueStatusDto
{
    /// <summary>当前占着全局名额的上传会话数（含暂停中的：它仍然占着暂存空间）</summary>
    public int ActiveUploads { get; set; }

    /// <summary>
    /// 执行队列里还没放行的项数（两种排队之和，保留给旧调用方）。
    ///
    /// 把两种排队混成一个数会读出错误的结论：界面显示
    /// 「0 个正在传 / 2 个排队中 · 有空余名额」，看起来就是系统卡住了——
    /// 而实际情况多半是那两项在等**本次执行**的并发度，与全局名额毫无关系。
    /// 新代码请用下面两个分开的字段。
    /// </summary>
    public int QueuedItems { get; set; }

    /// <summary>
    /// 在等全局上传名额的项数。只有这个数与 <see cref="GlobalLimit"/> 是同一件事，
    /// 「排队中但有空余名额」这句自相矛盾的话说的就是它。
    /// </summary>
    public int WaitingForUploadSlot { get; set; }

    /// <summary>
    /// 在等本次执行并发度的项数（前一项没跑完，按 max_concurrent 排着）。
    /// 这是计划「严格按顺序」的正常表现，不是异常。
    /// </summary>
    public int WaitingInRun { get; set; }

    /// <summary>系统设置 max_concurrent_uploads_total</summary>
    public int GlobalLimit { get; set; }
}

/// <summary>
/// 一条已经结束的传输。
///
/// 「传输中」页面只显示在途的会话，传完就消失。于是有两类信息在界面上无处可查：
///
///   一、**没传成的那些**（failed / cancelled / expired）。它们不入备份集（没归档），
///       也不在传输中（已终结）。要回答「昨晚那条为什么没传上去」只能翻日志或查库。
///   二、**只有会话上才有的数字**——这次传了多久、平均速度、重试了几次。
///       判断「链路够不够快」「限速要不要调」靠的是它，备份集上没有这些。
///
/// 已入库的那些也列进来，但只作为时间线上的一行：正式档案在「备份集」页面，
/// 这里给出 <see cref="BackupSetCode"/> 让人点过去，而不是把那一页复制一遍。
/// </summary>
public class FinishedTransferDto
{
    public Guid SessionId { get; set; }

    public Guid ClientId { get; set; }

    public string ClientDisplayName { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public string TaskName { get; set; } = string.Empty;

    /// <summary>业务单元（账套）名。老数据里候选可能没挂业务单元，此时为 null。</summary>
    public string? BusinessUnitName { get; set; }

    /// <summary>终态：committed / failed / cancelled / expired</summary>
    public string Status { get; set; } = string.Empty;

    public int TotalFiles { get; set; }

    public long TotalBytes { get; set; }

    /// <summary>实际传完的字节。失败的那些靠它与 TotalBytes 的差看出「传到哪儿断的」。</summary>
    public long UploadedBytes { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>整段传输耗时（秒）。起止有一个取不到时为 null，不猜。</summary>
    public long? DurationSeconds { get; set; }

    /// <summary>
    /// 全程平均速度（字节/秒）。这里用平均值是对的——传输已经结束，
    /// 要回答的是「这条链路整体多快」，不是「此刻快不快」。
    /// 耗时为 0 或算不出来时为 null。
    /// </summary>
    public long? AverageBytesPerSecond { get; set; }

    /// <summary>服务端记下的重试次数。链路不稳时它比平均速度更早露出来。</summary>
    public int RetryCount { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 入库之后的正式备份集编号；没入库、或那份备份已经被删掉时为 null。
    /// null 且状态是 committed，说明这次传输的成果已经不在了——
    /// 这一栏空着本身就是要给人看见的信息。
    /// </summary>
    public string? BackupSetCode { get; set; }
}
