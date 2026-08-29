namespace BackupMonitor.Core.Enums;

/// <summary>客户端状态</summary>
public enum ClientStatus
{
    PendingApproval,
    Online,
    SuspectedOffline,
    Offline,
    Disabled,
    Revoked,
    CertificateExpired
}

/// <summary>任务模式</summary>
public enum TaskMode
{
    Automatic,
    ApprovalRequired,
    Manual,
    MonitorOnly,
    Paused
}

/// <summary>识别类型</summary>
public enum RecognizerType
{
    LatestSingleFile,
    LatestDirectory,
    MultiFileSet,
    SubdirectoryUnits
}

/// <summary>预检状态</summary>
public enum PrecheckStatus
{
    NotScanned,
    Passed,
    NoNewBackup,
    StillChanging,
    RequiredFileMissing,
    SizeAbnormal,
    PathNotFound,
    AccessDenied,
    Failed
}

/// <summary>指令状态</summary>
public enum CommandStatus
{
    Pending,
    Claimed,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Expired,
    Rejected
}

/// <summary>指令类型</summary>
public enum CommandType
{
    PrecheckTask,
    PrecheckAll,
    UploadCandidate,
    UploadLatest,
    PauseUpload,
    ResumeUpload,
    CancelUpload,
    Rescan,
    Rehash,
    SyncConfig,
    RefreshMetrics,
    UpgradeAgent,

    /// <summary>
    /// 浏览客户端目录，返回一棵只含元数据的目录树。
    /// 建任务向导用它让管理员"指着"备份目录，而不是"描述"它。
    /// </summary>
    BrowsePath,

    /// <summary>
    /// 列出客户端上安装的 Windows 服务。
    /// 配关键服务监控时从真实列表里挑，而不是凭记忆敲 MSSQL$SQLEXPRESS 这种名字。
    /// </summary>
    ListServices,

    /// <summary>
    /// 主动探测这台机器上「像备份目录」的地方（B7）。
    ///
    /// 与 browse_path 的区别是方向：browse_path 是**定向**的（给一个路径，返回那棵树），
    /// 这一条是**发现**的（给一台机器，返回候选清单）。
    /// 它回答的问题是「这台机器上还有没有别的备份没被监控起来」——
    /// 而这个问题此前无法回答，只能人工一层层点开找。
    ///
    /// 边界比 browse_path 更严：**只返回候选目录本身的统计特征，不返回任何文件名**。
    /// </summary>
    ProbeBackupDirs
}

/// <summary>上传会话状态</summary>
public enum UploadStatus
{
    Created,
    WaitingPermission,
    Uploading,
    Paused,
    RetryWait,
    Received,
    Verifying,
    Verified,
    Committed,
    Failed,
    Cancelled,
    Expired
}

/// <summary>备份版本状态</summary>
public enum BackupSetStatus
{
    Verifying,
    Available,
    VerificationFailed,
    Quarantined,
    RecycleBin,
    Deleted
}

/// <summary>告警等级</summary>
public enum AlertLevel
{
    Critical,
    Warning,
    Notice
}

/// <summary>告警状态</summary>
public enum AlertStatus
{
    Open,
    Acknowledged,
    InProgress,
    Recovered,
    Closed,
    Ignored
}

/// <summary>用户状态</summary>
public enum UserStatus
{
    Active,
    Locked,
    Disabled
}

/// <summary>恢复请求状态</summary>
public enum RestoreRequestStatus
{
    Requested,
    Verifying,
    Ready,
    Downloading,
    Completed,
    Failed,
    Expired
}

/// <summary>审计结果</summary>
public enum AuditResult
{
    Success,
    Failure
}

/// <summary>注册令牌状态</summary>
public enum RegistrationTokenStatus
{
    Active,
    Revoked,
    Expired
}

/// <summary>证书状态</summary>
public enum CertificateStatus
{
    Active,
    Revoked,
    Expired,
    Superseded
}

/// <summary>上传文件状态</summary>
public enum UploadFileStatus
{
    Pending,
    Uploading,
    Received,
    Verified,
    Failed
}

/// <summary>上传分块状态</summary>
public enum UploadChunkStatus
{
    Pending,
    Received,
    Verified,
    Failed
}

/// <summary>服务期望状态</summary>
public enum ServiceExpectedState
{
    Running,
    Stopped
}

/// <summary>服务实际状态</summary>
public enum ServiceActualState
{
    Running,
    Stopped,
    Paused,
    NotFound
}

/// <summary>服务启动类型</summary>
public enum ServiceStartType
{
    Auto,
    Manual,
    Disabled
}

/// <summary>文件校验状态</summary>
public enum VerificationStatus
{
    Verified,
    Failed
}

/// <summary>批次状态</summary>
public enum BatchStatus
{
    Pending,
    Running,
    Completed,
    Partial,
    Failed,
    Cancelled
}

/// <summary>角色范围类型</summary>
public enum ScopeType
{
    Global,
    ClientGroup
}

/// <summary>通知渠道</summary>
public enum NotificationChannel
{
    Email,
    Wecom,
    Dingtalk
}

/// <summary>通知发送状态</summary>
public enum NotificationStatus
{
    Pending,
    Sent,
    Failed,

    /// <summary>
    /// 已取消（审计 G-12）：告警在这封通知发出去之前就恢复了。
    /// 派发器只看投递记录自身的状态，不回查告警——没有这一格的时候，
    /// 一次短暂抖动产生的邮件会在告警早已恢复之后继续重试三轮。
    /// </summary>
    Cancelled
}

/// <summary>重要等级</summary>
public enum ImportanceLevel
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>备份计划的调度方式（V028）</summary>
public enum PlanScheduleKind
{
    /// <summary>每天在 RunAt 时刻执行</summary>
    Daily,

    /// <summary>每周指定的几天在 RunAt 时刻执行</summary>
    Weekly
}

/// <summary>一次顺序执行的来源（V028）：备份计划到点，或一次批量上传</summary>
public enum ExecutionRunKind
{
    BackupPlan,
    UploadBatch,

    /// <summary>
    /// 人在任务列表里多选后点「立即备份」（D3）。单个任务点一次仍然走直接下发以保持即时反馈，
    /// 多选才建执行——十个任务发十条独立指令等于十个同时开传，服务端暂存盘扛不住。
    /// </summary>
    Manual
}

/// <summary>
/// 顺序执行队列里单项的状态（V028）。
/// timeout 与 failed 分开：前者是「等不到它了」（客户端关机/一直没回音），
/// 后者是「它明确失败了」——排障时要看的地方完全不同。
/// </summary>
public enum ExecutionItemStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Timeout,
    Skipped,
    Cancelled
}
