namespace BackupMonitor.Shared.Models.Agent;

/// <summary>Agent 配置响应（设计书 11.2）</summary>
public class AgentConfigResponse
{
    /// <summary>配置版本号</summary>
    public long Version { get; set; }

    public DateTime IssuedAt { get; set; }

    /// <summary>服务端对配置内容的签名（防篡改）</summary>
    public string Signature { get; set; } = null!;

    public List<AgentTaskConfigDto> Tasks { get; set; } = [];

    public List<AgentMonitoredServiceDto> MonitoredServices { get; set; } = [];

    public AgentGlobalSettingsDto GlobalSettings { get; set; } = new();
}

/// <summary>下发给 Agent 的任务配置</summary>
public class AgentTaskConfigDto
{
    public Guid TaskId { get; set; }
    public string Name { get; set; } = null!;
    public string ApplicationName { get; set; } = null!;
    public string SourcePath { get; set; } = null!;

    /// <summary>latest_single_file / latest_directory / multi_file_set / subdirectory_units</summary>
    public string RecognizerType { get; set; } = null!;

    /// <summary>automatic / approval_required / manual / monitor_only / paused</summary>
    public string TaskMode { get; set; } = null!;

    public bool Enabled { get; set; }

    public string? ScanSchedule { get; set; }
    public string? ScheduleTimezone { get; set; }
    public int StabilityIntervalSeconds { get; set; }
    public int MaxStabilityWaitSeconds { get; set; }
    public int? BandwidthLimitKbps { get; set; }
    public int ChunkSizeBytes { get; set; }

    /// <summary>
    /// 0~1440 分钟，用来错开多台客户端在同一 cron 时刻的扫描/上传洪峰。
    /// 字段位置固定在 ChunkSizeBytes 之后、RecognizerConfig 之前——
    /// AgentSignatureCanonicalizer 里的顺序与此一致，改动顺序会让配置签名不兼容。
    /// </summary>
    public int RandomDelayMinutes { get; set; }

    /// <summary>识别规则配置（jsonb 原样下发）</summary>
    public string RecognizerConfig { get; set; } = "{}";

    public long ConfigVersion { get; set; }

    /// <summary>
    /// 单文件在途分块数（1-16）。**性能参数，不是限速手段**——
    /// 限速的唯一口径是 <see cref="BandwidthLimitKbps"/>，那是管理员可见可调的那个。
    ///
    /// 下发它是因为它会随现场变（万兆网 4 路填不满、老机器或机械盘想降到 2），
    /// 而此前它只存在于客户端本地的 appsettings，改一次要逐台改文件、逐台重启服务。
    ///
    /// 字段位置追加在任务块末尾（见 AgentSignatureCanonicalizer 的同名说明）：
    /// 前面的顺序一个都不能动，否则配置签名不兼容。
    /// </summary>
    public int MaxParallelChunks { get; set; } = 4;

    /// <summary>同时在传的文件数（1-8）。不会放大在途分块总数，两者共用同一个信号量。</summary>
    public int MaxParallelFiles { get; set; } = 3;

    /// <summary>预检阶段并发读盘算 SHA-256 的文件数（1-8）。</summary>
    public int MaxParallelHashes { get; set; } = 3;

    /// <summary>
    /// 上一次候选备份集的 QuickFingerprint，按业务单元 externalKey 索引（无业务单元时为 root）。
    ///
    /// 给 Agent 的两段式扫描当基线用：先只算头尾采样的 QuickHash 拼出指纹，
    /// 与这里的值一致就直接回报 no_new_backup，不再把整份备份读一遍算 SHA-256。
    ///
    /// 由服务端下发而不是 Agent 自存：两处状态迟早漂移，而漂移的表现是
    /// 「明明有新备份却说没有」——那是这个系统最不能出的错。
    /// 因此它也必须进配置签名，能改写响应体的人不能借它让备份静默停摆。
    /// </summary>
    public List<AgentUnitFingerprintDto> LastQuickFingerprints { get; set; } = [];
}

/// <summary>某个业务单元上一次候选备份集的快速指纹</summary>
public class AgentUnitFingerprintDto
{
    /// <summary>业务单元 external_key；任务没有业务单元时固定为 root，与 BackupScanner 的 candidateKey 口径一致。</summary>
    public string ExternalKey { get; set; } = "root";

    public string QuickFingerprint { get; set; } = string.Empty;
}

/// <summary>下发给 Agent 的关键服务监控定义</summary>
public class AgentMonitoredServiceDto
{
    public string ServiceName { get; set; } = null!;
    public string DisplayName { get; set; } = null!;

    /// <summary>running / stopped</summary>
    public string ExpectedState { get; set; } = "running";

    public bool AlertOnMismatch { get; set; }
}

/// <summary>全局设置</summary>
public class AgentGlobalSettingsDto
{
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public int MaxConcurrentUploads { get; set; } = 2;
}
