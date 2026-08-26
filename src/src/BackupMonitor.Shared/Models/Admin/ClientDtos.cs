using BackupMonitor.Shared.Models;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>客户端列表查询参数（设计书 15.1）</summary>
public class ClientQuery : PagedQuery
{
    /// <summary>pending_approval / online / suspected_offline / offline / disabled / revoked</summary>
    public string? Status { get; set; }

    public Guid? GroupId { get; set; }
    public string? AgentVersion { get; set; }
    public bool? HasAlert { get; set; }
    public DateTime? LastHeartbeatBefore { get; set; }
}

/// <summary>客户端列表项</summary>
public class ClientListItemDto
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public Guid? ClientGroupId { get; set; }
    public string? ClientGroupName { get; set; }
    public string? OsName { get; set; }
    public string? AgentVersion { get; set; }

    /// <summary>客户端状态（snake_case）</summary>
    public string Status { get; set; } = null!;

    /// <summary>登记方式：lan_simple / secure。</summary>
    public string EnrollmentMode { get; set; } = "secure";

    public DateTime? CertificateExpiresAt { get; set; }
    public int? CertificateRemainingDays { get; set; }

    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int ActiveAlertCount { get; set; }
    public int TaskCount { get; set; }

    /// <summary>正在进行的上传会话数（created/uploading/verifying 等活动状态）。</summary>
    public int ActiveUploadCount { get; set; }

    /// <summary>已下发但还没跑完的指令数（pending/claimed/running），即预检、浏览目录、采样这类活。</summary>
    public int RunningCommandCount { get; set; }

    /// <summary>
    /// 运行状态：idle / uploading / working。
    ///
    /// 「在线」回答的是"连得上吗"，回答不了"现在能不能对它动手"——
    /// 正在上传的机器上再下发一次上传，服务端会以 CLIENT_BUSY 回绝；
    /// 禁用一台正在传的机器，会把传到一半的上传断在半路。
    /// 这个字段把"正在干活"这件事显式送到界面上，按钮据此上锁。
    /// </summary>
    public string RuntimeState { get; set; } = "idle";
}

/// <summary>客户端详情（设计书 15.2）</summary>
public class ClientDetailDto : ClientListItemDto
{
    public string? MachineId { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? IpAddresses { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? ApprovedByName { get; set; }
    public long LastConfigVersion { get; set; }
    public string? CertificateThumbprint { get; set; }
    public int? TimeOffsetSeconds { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long RowVersion { get; set; }

    public List<ClientDiskDto> Disks { get; set; } = [];
    public List<ClientServiceDto> MonitoredServices { get; set; } = [];
    public List<ClientUserSessionDto> UserSessions { get; set; } = [];
    public ClientMetricsDto? LastMetrics { get; set; }

    /// <summary>当前正在上传的任务名，用来把"运行中"说成一句人话。</summary>
    public List<string> ActiveUploadTaskNames { get; set; } = [];
}

/// <summary>最近自动登记客户端的待办摘要。</summary>
public sealed class RecentAutoEnrollmentDto
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
}

/// <summary>客户端磁盘</summary>
public class ClientDiskDto
{
    public string DriveName { get; set; } = null!;
    public string? Filesystem { get; set; }
    public long? TotalBytes { get; set; }
    public long? FreeBytes { get; set; }
    public bool IsSourceVolume { get; set; }
    public DateTime SampledAt { get; set; }
}

/// <summary>客户端关键服务（定义 + 最近状态）</summary>
public class ClientServiceDto
{
    public Guid DefinitionId { get; set; }
    public string ServiceName { get; set; } = null!;
    public string DisplayName { get; set; } = null!;

    /// <summary>running / stopped</summary>
    public string ExpectedState { get; set; } = null!;

    /// <summary>running / stopped / paused / not_found（最近采样）</summary>
    public string? ActualState { get; set; }

    public DateTime? LastSampledAt { get; set; }
    public bool AlertOnMismatch { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>客户端最近指标（管理端展示用）</summary>
public class ClientMetricsDto
{
    public decimal? CpuPercent { get; set; }
    public decimal? AgentCpuPercent { get; set; }
    public decimal? MemoryPercent { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? MemoryAvailableBytes { get; set; }
    public long? AgentMemoryBytes { get; set; }
    public long? NetworkSendBps { get; set; }
    public long? NetworkReceiveBps { get; set; }
    public long? AgentUptimeSeconds { get; set; }
    public long? SystemUptimeSeconds { get; set; }
    public DateTime? ReceivedAt { get; set; }
}

/// <summary>客户端资源指标历史点</summary>
public class ClientMetricsPointDto : ClientMetricsDto
{
    public int? ActiveCommandCount { get; set; }
    public int? ActiveUploadCount { get; set; }
}

/// <summary>客户端资源指标历史</summary>
public class ClientMetricsHistoryDto
{
    public Guid ClientId { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public List<ClientMetricsPointDto> Points { get; set; } = [];
}

/// <summary>客户端当前 Windows 用户会话</summary>
public class ClientUserSessionDto
{
    public int SessionId { get; set; }
    public string? Username { get; set; }
    public string? Domain { get; set; }
    public string State { get; set; } = null!;
    public string? ClientName { get; set; }
    public string? ClientAddress { get; set; }
    public bool IsRemote { get; set; }
    public DateTime? LogonAt { get; set; }
    public DateTime SampledAt { get; set; }
}

/// <summary>禁用客户端请求（设计书 15.4）</summary>
public class DisableClientRequest
{
    public string? Reason { get; set; }
}

/// <summary>重新启用被禁用的客户端。</summary>
public class EnableClientRequest
{
    public string? Reason { get; set; }
}

/// <summary>注销客户端请求（设计书 15.5，原因必填）</summary>
public class RevokeClientRequest
{
    public string Reason { get; set; } = null!;
}

/// <summary>审批通过响应（设计书 15.3，签发客户端证书）</summary>
public class ApproveClientResponseDto
{
    public Guid ClientId { get; set; }
    public string CertificateThumbprint { get; set; } = null!;
    public DateTime CertificateIssuedAt { get; set; }
    public DateTime CertificateExpiresAt { get; set; }
}

/// <summary>
/// 概览页的在线客户端资源一行。
///
/// 客户端列表接口不返回指标，只有详情才带——于是"现在哪台机器吃紧"这个问题，
/// 只能一台台点进详情去看。这是运维每天都要回答的问题，值得在概览上直接给出。
/// </summary>
public class ClientResourceRowDto
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime? LastHeartbeatAt { get; set; }

    public decimal? CpuPercent { get; set; }
    public decimal? MemoryPercent { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? MemoryAvailableBytes { get; set; }
    public long? AgentMemoryBytes { get; set; }
    public long? SystemUptimeSeconds { get; set; }

    /// <summary>备份源盘里最紧张的那块的可用比例；没有源盘时为空。</summary>
    public decimal? MinSourceDiskFreePercent { get; set; }
    public string? MinSourceDiskName { get; set; }

    public int ActiveAlertCount { get; set; }
}
