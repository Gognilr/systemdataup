using BackupMonitor.Core.Abstractions;
using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Client;

/// <summary>受管客户端（Agent 安装实例），对应 clients 表</summary>
public class Client : IHasRowVersion
{
    public Guid Id { get; set; }

    /// <summary>机器指纹（唯一，由 Agent 采集硬件/系统特征生成）</summary>
    public string MachineId { get; set; } = null!;

    /// <summary>主机名</summary>
    public string Hostname { get; set; } = null!;

    /// <summary>显示名称</summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>所属分组</summary>
    public Guid? ClientGroupId { get; set; }

    public string? OsName { get; set; }

    public string? OsVersion { get; set; }

    /// <summary>CPU 架构（x64/x86/arm64）</summary>
    public string? Architecture { get; set; }

    /// <summary>Agent 版本号</summary>
    public string? AgentVersion { get; set; }

    /// <summary>IP 地址列表（jsonb）</summary>
    public string? IpAddresses { get; set; }

    public ClientStatus Status { get; set; } = ClientStatus.PendingApproval;

    public DateTime? ApprovedAt { get; set; }

    /// <summary>审批人</summary>
    public Guid? ApprovedBy { get; set; }

    /// <summary>最后心跳时间</summary>
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>最后一次配置版本号（用于增量配置下发）</summary>
    public long LastConfigVersion { get; set; }

    /// <summary>当前证书指纹</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>当前证书到期时间</summary>
    public DateTime? CertificateExpiresAt { get; set; }

    /// <summary>客户端与服务端的时间偏差（秒）</summary>
    public int? TimeOffsetSeconds { get; set; }

    public string? Notes { get; set; }

    /// <summary>注册时提交的客户端公钥（Base64 SPKI），审批签发证书使用（V002）</summary>
    public string? PublicKey { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>乐观锁版本号</summary>
    public long RowVersion { get; set; }

    // 导航属性
    public ClientGroup? ClientGroup { get; set; }
    public Rbac.User? ApprovedByUser { get; set; }
    public ICollection<ClientCertificate> Certificates { get; set; } = [];
    public ICollection<ClientDisk> Disks { get; set; } = [];
    public ICollection<MonitoredServiceDefinition> MonitoredServices { get; set; } = [];
}
