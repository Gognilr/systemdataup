namespace BackupMonitor.Shared.Models.Agent;

/// <summary>
/// Agent 安装器的公开引导信息。这里只返回可公开的服务端签名公钥、
/// 服务端 TLS 证书和 Agent 发布包路径，不包含任何管理员凭据或注册令牌。
/// </summary>
public sealed class AgentBootstrapResponse
{
    /// <summary>服务端 RSA SubjectPublicKeyInfo 公钥（Base64）。</summary>
    public string ServerSigningPublicKey { get; set; } = null!;

    /// <summary>LAN Turnkey 服务端证书指纹（无分隔小写十六进制）。</summary>
    public string? ServerCertificateFingerprint { get; set; }

    /// <summary>LAN Turnkey 服务端根证书 PEM；Secure 形态不返回。</summary>
    public string? ServerCertificatePem { get; set; }

    /// <summary>相对于服务端地址的 Agent 发布包路径。</summary>
    public string AgentPackagePath { get; set; } = "/downloads/BackupMonitor.Agent.zip";

    /// <summary>服务端是否允许默认安装器走 LAN 自动登记。</summary>
    public bool AutomaticEnrollment { get; set; }

    /// <summary>当前部署模式：LanSimple 或 Secure。</summary>
    public string DeploymentMode { get; set; } = "Secure";

    /// <summary>引导协议版本，供未来安装器兼容性检查。</summary>
    public int ProtocolVersion { get; set; } = 1;

}
