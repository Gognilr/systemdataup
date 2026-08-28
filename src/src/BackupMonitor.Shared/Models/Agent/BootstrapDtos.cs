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

    /// <summary>
    /// 本服务端的实例 ID（Server:InstanceId，与 LAN 发现应答里的 ServerInstanceId 同一个值）。
    ///
    /// 之所以要在这条 HTTPS 接口上再给一遍：发现应答走的是无认证的明文 UDP，
    /// 谁都能广播；而这条连接的对端证书是被指纹固定过的。运行期地址重发现
    /// 拿它当「这台应答的机器是不是我原来那台服务端」的锚点，
    /// 锚点本身必须来自可信通道，否则整条校验是自证的。
    /// 旧版本服务端不返回它，客户端要能容忍为空。
    /// </summary>
    public string? ServerInstanceId { get; set; }
}
