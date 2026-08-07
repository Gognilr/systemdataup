using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Client;

/// <summary>关键服务实际状态（每次心跳采样），对应 client_service_states 表</summary>
public class ClientServiceState
{
    public Guid Id { get; set; }

    public Guid DefinitionId { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>实际状态</summary>
    public ServiceActualState ActualState { get; set; }

    /// <summary>启动类型</summary>
    public ServiceStartType StartType { get; set; } = ServiceStartType.Auto;

    public DateTime SampledAt { get; set; }

    // 导航属性
    public MonitoredServiceDefinition Definition { get; set; } = null!;
    public Client Client { get; set; } = null!;
}
