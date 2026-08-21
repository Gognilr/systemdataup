using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Agent;

/// <summary>客户端提交注册请求（设计书 10.1）</summary>
public class SubmitRegistrationRequest
{
    /// <summary>一次性注册令牌（Secure 模式使用；LanSimple 自动登记时为空）</summary>
    public string? RegistrationToken { get; set; }

    /// <summary>机器指纹（Agent 采集硬件/系统特征生成）</summary>
    [Required]
    public string MachineId { get; set; } = null!;

    [Required]
    public string Hostname { get; set; } = null!;

    public string? DisplayName { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? AgentVersion { get; set; }

    /// <summary>IP 地址列表</summary>
    public List<string>? IpAddresses { get; set; }

    /// <summary>客户端公钥（Base64，用于签发客户端证书）</summary>
    public string? PublicKey { get; set; }
}

/// <summary>提交注册响应（设计书 10.1）</summary>
public class SubmitRegistrationResponse
{
    /// <summary>注册记录 ID（= 新建客户端 ID，供轮询）</summary>
    public Guid RegistrationId { get; set; }

    /// <summary>pending_approval / approved / rejected</summary>
    public string Status { get; set; } = null!;

    /// <summary>建议的轮询间隔（秒）</summary>
    public int PollAfterSeconds { get; set; } = 30;
}

/// <summary>查询注册结果响应（设计书 10.2）</summary>
public class RegistrationResultResponse
{
    public Guid RegistrationId { get; set; }

    /// <summary>pending_approval / approved / rejected / revoked</summary>
    public string Status { get; set; } = null!;

    /// <summary>审批通过后返回客户端 ID</summary>
    public Guid? ClientId { get; set; }

    /// <summary>审批通过后签发的客户端证书（PEM）</summary>
    public string? CertificatePem { get; set; }

    /// <summary>证书指纹</summary>
    public string? CertificateThumbprint { get; set; }

    public DateTime? CertificateExpiresAt { get; set; }

    /// <summary>拒绝原因</summary>
    public string? RejectionReason { get; set; }

    /// <summary>仍待审批时建议的轮询间隔（秒）</summary>
    public int? PollAfterSeconds { get; set; }
}

/// <summary>客户端证书续签结果；Agent 使用现有私钥安装新证书。</summary>
public sealed class CertificateRenewalResponse
{
    public Guid ClientId { get; set; }
    public string CertificatePem { get; set; } = null!;
    public string CertificateThumbprint { get; set; } = null!;
    public DateTime CertificateIssuedAt { get; set; }
    public DateTime CertificateExpiresAt { get; set; }
}
