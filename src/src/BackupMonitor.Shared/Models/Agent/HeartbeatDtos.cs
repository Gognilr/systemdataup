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

    /// <summary>当前 Windows 登录会话</summary>
    public List<HeartbeatUserSessionDto>? UserSessions { get; set; }

    /// <summary>Agent 侧活动指令 ID 列表</summary>
    public List<Guid>? ActiveCommands { get; set; }

    /// <summary>Agent 侧活动上传会话 ID 列表</summary>
    public List<Guid>? ActiveUploads { get; set; }

    /// <summary>
    /// 磁盘 / 服务状态 / 用户会话三块快照与上一次心跳完全相同（审计 B-09）。
    ///
    /// 为 true 时 Disks / ServiceStates / UserSessions 一律为 null，不重复上报——
    /// 心跳每分钟一次，而这三块在绝大多数时间里一个字节都不会变。
    /// 服务端已经是「为 null 就不动库」的语义，唯一需要额外处理的是磁盘告警：
    /// 它必须回落到库里已有的记录继续判定，否则「快照没变」会变成「不再评估」。
    /// </summary>
    public bool SnapshotUnchanged { get; set; }
}

/// <summary>心跳负载指标</summary>
public class HeartbeatMetricsDto
{
    public decimal? CpuPercent { get; set; }
    public decimal? AgentCpuPercent { get; set; }
    public decimal? MemoryPercent { get; set; }
    public long? MemoryTotalBytes { get; set; }
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

/// <summary>心跳中的 Windows 用户会话</summary>
public class HeartbeatUserSessionDto
{
    public int SessionId { get; set; }
    public string? Username { get; set; }
    public string? Domain { get; set; }
    public string State { get; set; } = null!;
    public string? ClientName { get; set; }
    public string? ClientAddress { get; set; }
    public bool IsRemote { get; set; }
    public DateTime? LogonAt { get; set; }
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

    /// <summary>证书剩余有效期小于等于 30 天时提示 Agent 续签。</summary>
    public bool CertificateRenewalRequired { get; set; }

    /// <summary>Agent 托盘只显示的消息，不包含敏感凭据。</summary>
    public List<AgentNotificationDto> Notifications { get; set; } = [];
}

/// <summary>Agent 托盘消息。</summary>
public class AgentNotificationDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = null!;
    public string Severity { get; set; } = "info";
    public string Title { get; set; } = null!;
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; }
}
