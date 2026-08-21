namespace BackupMonitor.Agent;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:5080";
    /// <summary>LanSimple 默认启用：注册请求不携带一次性令牌，由服务端私网策略决定是否直接签证。</summary>
    public bool AutomaticEnrollment { get; set; }
    public string? RegistrationToken { get; set; }
    /// <summary>服务端 RSA 签名公钥（Base64 编码的 SubjectPublicKeyInfo）</summary>
    public string? ServerSigningPublicKey { get; set; }
    /// <summary>LAN Turnkey 服务端证书指纹；非空时所有 HTTPS 请求都执行指纹固定。</summary>
    public string? ServerCertificateFingerprint { get; set; }
    /// <summary>仅限本地联调；生产默认拒绝未验签的指令和配置。</summary>
    public bool AllowUnsignedCommands { get; set; }
    public string? DisplayName { get; set; }
    public string DataDirectory { get; set; } = "%ProgramData%\\BackupMonitor\\Agent";
    public string AgentVersion { get; set; } = "1.0.0";
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public int CommandPollIntervalSeconds { get; set; } = 10;
    public bool AllowInsecureTls { get; set; }
    /// <summary>状态目录 ACL 开关；生产默认启用，仅测试可显式关闭。</summary>
    public bool EnforceAcl { get; set; } = true;
    public int MaxUploadRetries { get; set; } = 3;

    public string ExpandedDataDirectory =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataDirectory));

    /// <summary>安装阶段短期使用的一次性注册令牌文件；注册成功后 Agent 会立即删除。</summary>
    public string ExpandedRegistrationTokenPath =>
        Path.Combine(ExpandedDataDirectory, "registration-token.txt");
}
