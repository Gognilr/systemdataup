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
    public string AgentVersion { get; set; } = "1.1.0";
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public int CommandPollIntervalSeconds { get; set; } = 10;
    public bool AllowInsecureTls { get; set; }
    /// <summary>状态目录 ACL 开关；生产默认启用，仅测试可显式关闭。</summary>
    public bool EnforceAcl { get; set; } = true;
    public int MaxUploadRetries { get; set; } = 3;

    /// <summary>
    /// 单个文件同时在途的分块数（1–16，超出范围会被夹回）。
    ///
    /// 为什么默认 4：一块的周期里真正占用网络的只有传输那一小段，其余都在
    /// 读盘、算哈希、等服务端写盘落库。串行（=1）时链路利用率只有个位数百分比，
    /// 4 路基本能把千兆填满，再往上收益递减、而客户端内存占用（每路一个分块缓冲）
    /// 和服务端并发压力是线性涨的。
    ///
    /// 要控制上传对网络的影响，用任务上的 BandwidthLimitKbps，那是服务端下发的、
    /// 管理员可见可调的口径；这个值是客户端本地的性能参数，不该拿来当限速用。
    /// </summary>
    public int MaxParallelChunks { get; set; } = 4;

    /// <summary>本地文件日志保留天数。出问题的机器常在客户现场，日志得留得住又不能撑爆磁盘。</summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>
    /// 允许被 browse_path 指令浏览的根路径白名单。
    ///
    /// 留空表示"本机全部固定磁盘"——这是默认值，因为备份目录可能在任何盘上，
    /// 而管理员本来就有权给这台机器建指向任意路径的备份任务。
    /// 高安全环境可以收紧到具体几个目录，届时 Agent 只允许浏览这些目录及其子目录。
    /// </summary>
    public string[] BrowseRoots { get; set; } = [];

    /// <summary>browse_path 单次最多返回的条目数上限（指令参数再大也不超过它）。</summary>
    public int BrowseMaxEntries { get; set; } = 20000;

    /// <summary>browse_path 单次最大抓取深度上限。</summary>
    public int BrowseMaxDepth { get; set; } = 6;

    /// <summary>browse_path 单次枚举的时间上限（秒）。超时按截断处理，而不是让指令一直挂着。</summary>
    public int BrowseTimeoutSeconds { get; set; } = 30;

    public string ExpandedDataDirectory =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataDirectory));

    /// <summary>安装阶段短期使用的一次性注册令牌文件；注册成功后 Agent 会立即删除。</summary>
    public string ExpandedRegistrationTokenPath =>
        Path.Combine(ExpandedDataDirectory, "registration-token.txt");
}
