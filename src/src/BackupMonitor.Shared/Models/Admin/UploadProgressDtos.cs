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
