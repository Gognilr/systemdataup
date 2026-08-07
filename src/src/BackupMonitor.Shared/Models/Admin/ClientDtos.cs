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

    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int ActiveAlertCount { get; set; }
    public int TaskCount { get; set; }
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
    public DateTime? CertificateExpiresAt { get; set; }
    public int? TimeOffsetSeconds { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long RowVersion { get; set; }

    public List<ClientDiskDto> Disks { get; set; } = [];
    public List<ClientServiceDto> MonitoredServices { get; set; } = [];
    public ClientMetricsDto? LastMetrics { get; set; }
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
    public decimal? MemoryPercent { get; set; }
    public long? MemoryAvailableBytes { get; set; }
    public long? AgentMemoryBytes { get; set; }
    public DateTime? ReceivedAt { get; set; }
}

/// <summary>禁用客户端请求（设计书 15.4）</summary>
public class DisableClientRequest
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
