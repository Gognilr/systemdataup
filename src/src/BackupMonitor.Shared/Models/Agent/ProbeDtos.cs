namespace BackupMonitor.Shared.Models.Agent;

/// <summary>
/// probe_backup_dirs 指令参数（服务端 → Agent）。
///
/// 深度不是靠一个固定值控制成本的，靠**预算 + 命中即止**：
/// 深度本身不是成本，条目数才是，而备份盘上的条目数集中在最深的一两层。
/// 默认深度 4 能看到「盘符 → 应用目录 → 备份目录 → 日期目录」这条最常见的四层路径，
/// 实际枚举量小于无脑深度 3。
/// </summary>
public class ProbeBackupDirsCommandPayload
{
    /// <summary>限定只扫这几个盘（`D:\`）。留空 = 全部固定磁盘。</summary>
    public List<string>? Roots { get; set; }

    /// <summary>向下探测的层数。Agent 端夹在 1–6。</summary>
    public int MaxDepth { get; set; } = 4;

    /// <summary>最多返回几个候选。</summary>
    public int MaxCandidates { get; set; } = 20;

    /// <summary>单个盘符的时间闸（秒）。</summary>
    public int PerDriveTimeoutSeconds { get; set; } = 10;

    /// <summary>全机的时间闸（秒）。超时把**已经算出来的候选原样返回**并标「未扫完」。</summary>
    public int TotalTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// probe_backup_dirs 执行结果（Agent → 服务端）。
///
/// 只有候选目录本身的统计特征，**没有任何文件名**——这一条比 browse_path 更严：
/// browse_path 是人指着一个路径要看里面有什么，这一条是系统主动扫全盘，
/// 两者可接受的信息量不同。
/// </summary>
public class ProbeBackupDirsResultDto
{
    public List<ProbeCandidateDto> Candidates { get; set; } = [];

    /// <summary>实际探测过的盘符。</summary>
    public List<string> ScannedRoots { get; set; } = [];

    /// <summary>因为条目上限或时间闸而没扫完。已经算出的候选照常返回。</summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// 枚举时被拒绝访问的目录。不静默吞掉：「这里没有备份」和「这里我看不见」
    /// 对使用者是完全不同的两件事。
    /// </summary>
    public List<string> DeniedPaths { get; set; } = [];

    public int ScannedDirectories { get; set; }

    public DateTime CapturedAt { get; set; }

    public long ElapsedMs { get; set; }
}

/// <summary>一个「看起来像备份目录」的候选。</summary>
public class ProbeCandidateDto
{
    public string Path { get; set; } = string.Empty;

    /// <summary>0–100 的分数，只用于排序。</summary>
    public int Score { get; set; }

    /// <summary>
    /// 命中的信号，人话。**必须有**：人要看得懂它为什么被列出来，
    /// 否则这张表就是一串没有依据的路径，跟随便猜没有区别。
    /// </summary>
    public List<string> Signals { get; set; } = [];

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public DateTime? NewestFileAt { get; set; }

    /// <summary>下一层是不是日期目录（复用 DateName.IsDateLayer）。</summary>
    public bool HasDateLayer { get; set; }

    /// <summary>枚举这个目录时被拒绝访问——它仍然要出现在结果里。</summary>
    public bool AccessDenied { get; set; }
}
