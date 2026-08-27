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

    /// <summary>
    /// Agent 自报的本机网卡地址列表（jsonb 字符串数组）。
    ///
    /// 原先只在注册时写一次，之后永不刷新；现在心跳会在网卡列表变化时带上并覆盖。
    /// 注意它是「机器上有哪些地址」，不是「服务端从哪个地址收到的心跳」——后者见 <see cref="LastRemoteIp"/>。
    /// </summary>
    public string? IpAddresses { get; set; }

    /// <summary>
    /// 服务端最近一次心跳观测到的对端 IP。
    ///
    /// 与 <see cref="IpAddresses"/> 互补：多网卡机器自报一串地址，
    /// 但到底哪个真正连得通、当前在用哪个，只有服务端看到的这个能回答；
    /// 它也永远是最新的，不依赖 Agent 是否重新上报。
    /// </summary>
    public string? LastRemoteIp { get; set; }

    public ClientStatus Status { get; set; } = ClientStatus.PendingApproval;

    /// <summary>登记方式：lan_simple / secure。</summary>
    public string EnrollmentMode { get; set; } = "secure";

    public DateTime? ApprovedAt { get; set; }

    /// <summary>审批人</summary>
    public Guid? ApprovedBy { get; set; }

    /// <summary>最后心跳时间</summary>
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>最后一次配置版本号（用于增量配置下发）</summary>
    public long LastConfigVersion { get; set; }

    /// <summary>
    /// 客户端级配置修订号。监控服务定义的增删改会推进它。
    ///
    /// 必须与任务版本一起参与 requiredConfigVersion 的计算：配置下发里包含监控服务，
    /// 但它们的变更不会改动任何任务的 config_version，只看任务版本的话，
    /// 新加的监控服务永远到不了客户端，而且一声不响。
    /// </summary>
    public long ConfigRevision { get; set; }

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
