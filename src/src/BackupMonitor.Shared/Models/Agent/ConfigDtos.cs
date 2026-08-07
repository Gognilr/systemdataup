namespace BackupMonitor.Shared.Models.Agent;

/// <summary>Agent 配置响应（设计书 11.2）</summary>
public class AgentConfigResponse
{
    /// <summary>配置版本号</summary>
    public long Version { get; set; }

    public DateTime IssuedAt { get; set; }

    /// <summary>服务端对配置内容的签名（防篡改）</summary>
    public string Signature { get; set; } = null!;

    public List<AgentTaskConfigDto> Tasks { get; set; } = [];

    public List<AgentMonitoredServiceDto> MonitoredServices { get; set; } = [];

    public AgentGlobalSettingsDto GlobalSettings { get; set; } = new();
}

/// <summary>下发给 Agent 的任务配置</summary>
public class AgentTaskConfigDto
{
    public Guid TaskId { get; set; }
    public string Name { get; set; } = null!;
    public string ApplicationName { get; set; } = null!;
    public string SourcePath { get; set; } = null!;

    /// <summary>latest_single_file / latest_directory / multi_file_set / subdirectory_units</summary>
    public string RecognizerType { get; set; } = null!;

    /// <summary>automatic / approval_required / manual / monitor_only / paused</summary>
    public string TaskMode { get; set; } = null!;

    public bool Enabled { get; set; }

    public string? ScanSchedule { get; set; }
    public string? ScheduleTimezone { get; set; }
    public int StabilityIntervalSeconds { get; set; }
    public int MaxStabilityWaitSeconds { get; set; }
    public int? BandwidthLimitKbps { get; set; }
    public int ChunkSizeBytes { get; set; }

    /// <summary>识别规则配置（jsonb 原样下发）</summary>
    public string RecognizerConfig { get; set; } = "{}";

    public long ConfigVersion { get; set; }
}

/// <summary>下发给 Agent 的关键服务监控定义</summary>
public class AgentMonitoredServiceDto
{
    public string ServiceName { get; set; } = null!;
    public string DisplayName { get; set; } = null!;

    /// <summary>running / stopped</summary>
    public string ExpectedState { get; set; } = "running";

    public bool AlertOnMismatch { get; set; }
}

/// <summary>全局设置</summary>
public class AgentGlobalSettingsDto
{
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public int MaxConcurrentUploads { get; set; } = 2;
}
