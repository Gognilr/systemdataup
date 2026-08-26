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
    ListServices
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
    Failed
}

/// <summary>重要等级</summary>
public enum ImportanceLevel
{
    Low,
    Normal,
    High,
    Critical
}
