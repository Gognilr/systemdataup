using System.Text.Json.Serialization;
using BackupMonitor.Shared.Models;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>客户端列表查询参数（设计书 15.1）</summary>
public class ClientQuery : PagedQuery
{
    /// <summary>pending_approval / online / suspected_offline / offline / disabled / revoked</summary>
    public string? Status { get; set; }

    /// <summary>
    /// 是否把已注销的机器一起列出来。默认 false。
    ///
    /// 注销是终点：证书吊销、不再接受心跳、任何指令都会被 409 回绝。
    /// 一台重装后重新登记的机器会在列表里留下一条同名的注销记录，
    /// 默认列出来的后果是「新建任务时两条同名机器分不清该选哪个」。
    /// 显式按 status=revoked 查询时不受这个开关影响。
    /// </summary>
    public bool IncludeRevoked { get; set; }

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

    /// <summary>
    /// 服务端最近一次收到这台机器任何已认证请求的时间。存活判定取它与心跳的较晚者——
    /// 界面上的「在线 / 离线」显示的是这个，而不是单看心跳。
    /// </summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// 服务端最近一次心跳观测到的对端 IP。
    ///
    /// 放进列表项而不是只放详情：找一台机器最常用的线索就是 IP，
    /// 为看一眼 IP 逐台点进详情抽屉是没有道理的。
    /// 选它而不选 Agent 自报的网卡列表，是因为它只有一个值、永远最新，
    /// 而且必然是真正连得通的那个——列表里放一串地址既挤又要人自己猜。
    /// </summary>
    public string? LastRemoteIp { get; set; }

    /// <summary>
    /// 这台机器"应该按哪个地址去找"的 IPv4。
    ///
    /// 对端地址不一定是 IPv4：Windows 的名称解析常把服务端解析成 fe80:: 链路本地地址，
    /// 于是 Agent 走 IPv6 连上来，<see cref="LastRemoteIp"/> 就成了一串既不能 ping
    /// 也不能远程桌面的东西。IP 列存在的意义是"我照着它去连那台机器"，
    /// 所以这里给出可用的 IPv4：对端地址本身是 IPv4 就是它，否则回落到自报网卡里的第一个 IPv4。
    /// 纯 IPv6 环境下为空，界面此时仍显示对端地址而不是"—"。
    /// </summary>
    public string? Ipv4Address { get; set; }

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
    /// <summary>
    /// Agent 自报的本机网卡地址列表。
    ///
    /// 这里是解析后的数组，不是库里的 jsonb 原文——原先直接把原文透出去，
    /// 界面上显示的就是带方括号和引号的 ["192.168.1.37","fe80::…"]。
    /// jsonb 是存储细节，不该穿过 API 露给前端。
    /// </summary>
    public List<string> IpAddresses { get; set; } = [];

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

/// <summary>
/// 修改客户端的显示名称与所属分组。
///
/// 两个字段都是可选的，语义靠"传没传"区分而不是靠值：
/// 字段缺省表示不改这一项，<c>clientGroupId</c> 显式传 null 表示把机器移出分组。
/// 少了这个区分，"只改名字"就会顺手把分组清掉。
/// System.Text.Json 只在 JSON 里出现该属性时才调用 setter，
/// 下面两个 Specified 标志正是靠这一点记录"调用方到底提没提这个字段"。
/// </summary>
public class UpdateClientRequest
{
    private string? _displayName;
    private Guid? _clientGroupId;

    /// <summary>新的显示名称；不传表示不改。</summary>
    public string? DisplayName
    {
        get => _displayName;
        set
        {
            _displayName = value;
            DisplayNameSpecified = true;
        }
    }

    /// <summary>新的分组；不传表示不改，显式传 null 表示移出分组。</summary>
    public Guid? ClientGroupId
    {
        get => _clientGroupId;
        set
        {
            _clientGroupId = value;
            ClientGroupIdSpecified = true;
        }
    }

    [JsonIgnore]
    public bool DisplayNameSpecified { get; private set; }

    [JsonIgnore]
    public bool ClientGroupIdSpecified { get; private set; }
}

/// <summary>
/// 分组选项，只给"改分组"这类下拉框用。
///
/// 客户端列表里的 clientGroupName 只能告诉你当前这一页出现过哪些分组，
/// 拿它当候选集会漏掉所有还没有机器的分组。
/// </summary>
public class ClientGroupOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Code { get; set; } = null!;
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
