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
    /// <summary>
    /// appsettings.json 里的版本号。**不要直接拿它上报**，用 <see cref="ResolvedAgentVersion"/>。
    ///
    /// 踩过的坑（R20）：远程升级会保留本机原有的 appsettings.json（里面有服务端地址、
    /// 签名公钥、证书指纹，盖掉就等于把这台机器变成失联），于是这个值在升级之后
    /// **仍然是旧版本号**。而服务端判「升级成功」看的正是客户端自报的版本号——
    /// 照着这个字段报，每一次成功的升级都会被判成失败。
    /// </summary>
    public string AgentVersion { get; set; } = "1.3.0";

    private string? _resolvedAgentVersion;

    /// <summary>
    /// 实际生效的版本号：取正在运行的这个程序集自己的版本，读不到才退回配置值。
    ///
    /// 「运行的是哪一版」这个问题只有二进制本身答得准。配置文件会被保留、会被手工改、
    /// 会跟发布物分家，而这里要回答的恰恰是「换过去了没有」。
    /// </summary>
    public string ResolvedAgentVersion => _resolvedAgentVersion ??= ResolveAgentVersion();

    private string ResolveAgentVersion()
    {
        try
        {
            var informational = typeof(AgentOptions).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            // 信息版本号常带构建后缀（1.2.0+abc123），服务端比对时会截断，
            // 但库里那一列是 varchar(64)，早点截干净省得两边各截一次。
            var core = informational?.Split('+')[0].Trim();
            return string.IsNullOrWhiteSpace(core) ? AgentVersion : core;
        }
        catch (Exception)
        {
            return AgentVersion;
        }
    }
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

    /// <summary>
    /// 同时在传的文件数（1–8，超出范围会被夹回）。
    ///
    /// 为什么需要它：块内并发解决的是「等一块被处理完才敢发下一块」，而文件之间还停在
    /// 同一个问题上——每个文件固定三次串行往返（missing-chunks → PUT → complete）。
    /// 大文件无所谓，往返被摊薄进几百兆数据里；U8 附件库那种「几千个几十 KB 的文件」
    /// 则完全相反，速率由往返次数而不是带宽决定，网卡基本是空的。
    ///
    /// 它**不会**放大在途分块数：所有文件的分块 PUT 共用一个 MaxParallelChunks 大小的
    /// 信号量，文件级并发带来的只是控制类往返的重叠。
    /// </summary>
    public int MaxParallelFiles { get; set; } = 3;

    /// <summary>
    /// 预检阶段同时读盘算 SHA-256 的文件数（1–8，超出范围会被夹回）。
    ///
    /// 默认 3：单流顺序读通常吃不满盘，而并发太多会让机械盘退化成随机读、比串行还慢。
    /// </summary>
    public int MaxParallelHashes { get; set; } = 3;

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

    /// <summary>
    /// 升级执行器的显式路径（R20）。留空时按约定去安装目录的同级 Updater 目录里找。
    ///
    /// 留这个口子是给非标准部署用的（手工把 Agent 放在别处）。
    /// 它不是安全边界：执行器由本机管理员安装，与 Agent 自身同一信任域。
    /// </summary>
    public string? UpdaterPath { get; set; }

    /// <summary>
    /// 升级前要求的空闲程度：距离下一次计划扫描至少还有这么多分钟（R20）。
    ///
    /// 这就是原注释里说的「受控」。一刀切下去换文件会让传了一半的备份作废，
    /// 而备份窗口通常就是现场最不能打扰的那段时间。
    /// </summary>
    public int UpgradeIdleMarginMinutes { get; set; } = 15;

    public string ExpandedDataDirectory =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataDirectory));

    /// <summary>安装阶段短期使用的一次性注册令牌文件；注册成功后 Agent 会立即删除。</summary>
    public string ExpandedRegistrationTokenPath =>
        Path.Combine(ExpandedDataDirectory, "registration-token.txt");
}
