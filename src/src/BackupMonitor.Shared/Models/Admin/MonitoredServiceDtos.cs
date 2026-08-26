using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>新增一条关键 Windows 服务监控。</summary>
public class CreateMonitoredServiceRequest
{
    /// <summary>Windows 服务名（SCM 名称，不是显示名）。</summary>
    [Required]
    [StringLength(128)]
    public string ServiceName { get; set; } = null!;

    /// <summary>界面上的称呼；留空则沿用服务名。</summary>
    [StringLength(255)]
    public string? DisplayName { get; set; }

    /// <summary>running / stopped</summary>
    public string ExpectedState { get; set; } = "running";

    /// <summary>状态与期望不符时是否告警。</summary>
    public bool AlertOnMismatch { get; set; } = true;

    public bool Enabled { get; set; } = true;
}

/// <summary>修改一条关键 Windows 服务监控。服务名不可改——改名等于换一个监控对象。</summary>
public class UpdateMonitoredServiceRequest
{
    [StringLength(255)]
    public string? DisplayName { get; set; }

    public string ExpectedState { get; set; } = "running";

    public bool AlertOnMismatch { get; set; } = true;

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// list_services 指令参数（服务端 → Agent）。
/// </summary>
public class ListServicesCommandPayload
{
    /// <summary>
    /// 是否连同已停止的服务一起返回。
    /// 默认 true：要监控的服务此刻恰好停着，正是最需要把它加进来的时候。
    /// </summary>
    public bool IncludeStopped { get; set; } = true;
}

/// <summary>list_services 执行结果（Agent → 服务端）。</summary>
public class InstalledServiceListDto
{
    public DateTime CapturedAt { get; set; }
    public List<InstalledServiceDto> Services { get; set; } = [];
}

/// <summary>客户端上真实存在的一个 Windows 服务。</summary>
public class InstalledServiceDto
{
    /// <summary>SCM 服务名。</summary>
    public string ServiceName { get; set; } = null!;

    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>running / stopped / paused / …</summary>
    public string Status { get; set; } = null!;

    /// <summary>automatic / manual / disabled / …（读不到时为 null）</summary>
    public string? StartType { get; set; }

    /// <summary>
    /// 是否已经被这个客户端监控。服务端填充，Agent 不知道也不需要知道。
    /// </summary>
    public bool AlreadyMonitored { get; set; }
}

/// <summary>服务列表查询结果（含指令状态，界面据此轮询）。</summary>
public class ListServicesResultDto
{
    public Guid CommandId { get; set; }
    public string Status { get; set; } = null!;
    public string? ResultCode { get; set; }
    public string? ResultMessage { get; set; }
    public InstalledServiceListDto? Result { get; set; }
}
