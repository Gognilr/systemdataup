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
    /// <summary>
    /// 维护窗口的结束时刻；为空表示当前不在维护。
    ///
    /// 维护期间这台机器的告警只累加不通知——停机检修时不该有人被半夜叫醒。
    /// 但它**必须有结束时刻**：没有到期时间的维护模式是个经典陷阱，
    /// 有人开了忘了关，那台机器几个月没人监控，而界面上一切正常。
    /// </summary>
    public DateTime? MaintenanceUntil { get; set; }

    /// <summary>维护原因。三个月后回头看，「为什么那天静默了」是唯一想知道的事。</summary>
    public string? MaintenanceReason { get; set; }

    public string? OsName { get; set; }

    /// <summary>
    /// 系统版本（如 "Microsoft Windows Server 2016 Standard"）。
    /// 只有 OsName 时列表上一整列都是「Windows」，等于没有信息——
    /// 要判断哪几台还停在老系统上，靠的是这一列。
    /// </summary>
    public string? OsVersion { get; set; }

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

    /// <summary>
    /// 升级执行器版本。为空表示没装（老包装上来的机器，只能人工升级）或还没报过。
    /// 它和 Agent 版本是分开升级的，可以差很多——这一行就是拿来看这个差的。
    /// </summary>
    public string? UpdaterVersion { get; set; }

    // OsVersion 现在由 ClientListItemDto 提供（列表页也要显示系统版本），这里不再重复声明。

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

    /// <summary>系统名与版本，口径与客户端列表页一致。</summary>
    public string? OsName { get; set; }

    /// <inheritdoc cref="ClientListItemDto.OsVersion"/>
    public string? OsVersion { get; set; }

    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>
    /// 最近一次收到这台机器任何请求的时间（领指令、报进度、传分块都算）。
    ///
    /// 与 <see cref="LastHeartbeatAt"/> 一起决定界面上的「最近通信」：心跳只是每分钟
    /// 一次的例行汇报，一台正在传大文件、心跳被哈希拖住的机器，只看心跳会显得很久没动静。
    /// 口径与客户端列表页一致——同一台机器在两个页面上显示两个时间，比不显示更糟。
    /// </summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// 服务端最近一次心跳观测到的对端 IP。与客户端列表页同源同口径——
    /// 概览上看出"哪台吃紧"之后，下一步就是照着 IP 连过去看，
    /// 为一个地址再跳一次客户端列表是没有道理的。
    /// </summary>
    public string? LastRemoteIp { get; set; }

    /// <summary>
    /// 这台机器"应该按哪个地址去找"的 IPv4（对端地址是 IPv4 就用它，否则回落到自报网卡里的第一个）。
    /// 口径与 <see cref="ClientListItemDto.Ipv4Address"/> 完全一致：同一台机器在概览和客户端列表上
    /// 显示两个不同的地址，比不显示更糟。
    /// </summary>
    public string? Ipv4Address { get; set; }

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

/// <summary>
/// Agent 版本漂移现状（R11）。
///
/// 「随附版本」= 当前这台服务端发出去的客户端包是哪一版。
/// 界面靠它把落后的机器标出来，告警靠它判定「落后太久了」。
/// </summary>
public class AgentVersionStatusDto
{
    /// <summary>服务端当前随附的 Agent 版本；读不出来时为空，此时一律不判漂移。</summary>
    public string? BundledVersion { get; set; }

    /// <summary>这一版从什么时候起在这台服务端上可用（= 版本号文件的写入时刻）。</summary>
    public DateTime? BundledAvailableSince { get; set; }

    /// <summary>落后超过这个天数才告警。</summary>
    public int DriftAlertDays { get; set; }

    /// <summary>当前已经落后阈值天数（告警条件成立）。</summary>
    public bool Overdue { get; set; }

    /// <summary>低于随附版本、或从未上报过版本号的机器。</summary>
    public List<AgentVersionDriftItemDto> OutdatedClients { get; set; } = [];
}

/// <summary>进入维护模式的请求。</summary>
public class StartMaintenanceRequest
{
    /// <summary>维护多少小时。必须有值——没有到期时间的维护模式会被人忘掉。</summary>
    public int Hours { get; set; } = 4;

    /// <summary>维护原因，必填。</summary>
    public string Reason { get; set; } = null!;
}

/// <summary>一台落后的客户端。</summary>
public class AgentVersionDriftItemDto
{
    public Guid ClientId { get; set; }
    public string Hostname { get; set; } = null!;
    public string? DisplayName { get; set; }

    /// <summary>它自报的版本；从未上报过时为空。</summary>
    public string? AgentVersion { get; set; }

    /// <summary>它自报的升级执行器版本；没装或还没报过时为空。</summary>
    public string? UpdaterVersion { get; set; }
}
