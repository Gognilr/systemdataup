using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Agent;

/// <summary>心跳请求（设计书 11.1）</summary>
public class HeartbeatRequest
{
    /// <summary>客户端本地时间</summary>
    public DateTime? ClientTime { get; set; }

    public long? AgentUptimeSeconds { get; set; }
    public long? SystemUptimeSeconds { get; set; }

    /// <summary>客户端当前持有的配置版本号</summary>
    public long ConfigVersion { get; set; }

    public HeartbeatMetricsDto? Metrics { get; set; }

    public List<HeartbeatDiskDto>? Disks { get; set; }

    public List<HeartbeatServiceStateDto>? ServiceStates { get; set; }

    /// <summary>Agent 侧活动指令 ID 列表</summary>
    public List<Guid>? ActiveCommands { get; set; }

    /// <summary>Agent 侧活动上传会话 ID 列表</summary>
    public List<Guid>? ActiveUploads { get; set; }
}

/// <summary>心跳负载指标</summary>
public class HeartbeatMetricsDto
{
    public decimal? CpuPercent { get; set; }
    public decimal? MemoryPercent { get; set; }
    public long? MemoryAvailableBytes { get; set; }
    public long? AgentMemoryBytes { get; set; }
    public long? NetworkSendBps { get; set; }
    public long? NetworkReceiveBps { get; set; }
}

/// <summary>心跳磁盘状态</summary>
public class HeartbeatDiskDto
{
    [Required]
    public string DriveName { get; set; } = null!;
    public string? VolumeLabel { get; set; }
    public string? Filesystem { get; set; }
    public long? TotalBytes { get; set; }
    public long? FreeBytes { get; set; }
    public bool IsSourceVolume { get; set; }
}

/// <summary>心跳关键服务状态</summary>
public class HeartbeatServiceStateDto
{
    [Required]
    public string ServiceName { get; set; } = null!;

    /// <summary>running / stopped / paused / not_found</summary>
    [Required]
    public string ActualState { get; set; } = null!;

    /// <summary>auto / manual / disabled</summary>
    public string? StartType { get; set; }
}

/// <summary>心跳响应（设计书 11.1）</summary>
public class HeartbeatResponse
{
    /// <summary>服务端时间（客户端校时依据）</summary>
    public DateTime ServerTime { get; set; }

    /// <summary>服务端最新配置版本（大于客户端版本时需拉取配置）</summary>
    public long RequiredConfigVersion { get; set; }

    /// <summary>是否有待领取指令</summary>
    public bool CommandsAvailable { get; set; }

    /// <summary>心跳间隔（秒）</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;
}
