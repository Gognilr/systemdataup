using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Client;

/// <summary>需要监控的 Windows 服务定义，对应 monitored_service_definitions 表</summary>
public class MonitoredServiceDefinition
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>Windows 服务名（服务控制管理器名称）</summary>
    public string ServiceName { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>期望状态</summary>
    public ServiceExpectedState ExpectedState { get; set; } = ServiceExpectedState.Running;

    /// <summary>状态不匹配时是否触发告警</summary>
    public bool AlertOnMismatch { get; set; } = true;

    public bool Enabled { get; set; } = true;

    /// <summary>最近一次上报的当前状态快照；历史表只记录状态发生变化的时刻。</summary>
    public ServiceActualState? CurrentActualState { get; set; }

    public ServiceStartType? CurrentStartType { get; set; }

    public DateTime? CurrentSampledAt { get; set; }

    // 导航属性
    public Client Client { get; set; } = null!;
    public ICollection<ClientServiceState> ServiceStates { get; set; } = [];
}
