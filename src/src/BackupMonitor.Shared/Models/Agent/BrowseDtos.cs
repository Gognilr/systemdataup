namespace BackupMonitor.Shared.Models.Agent;

/// <summary>
/// browse_path 指令参数（服务端 → Agent，原样进 commands.payload）。
///
/// 设计要点：一次抓一棵子树，而不是一次一层。
/// Agent 的指令轮询间隔是 10 秒，"点一个目录、发一条指令、等一次轮询"意味着每次点击
/// 要等 5–10 秒——那比手写 JSON 还难用。一次把前几层全部取回来，浏览器端展开就是零延迟。
/// 备份目录的结构信息几乎全部落在前 3 层（业务单元 / 日期 / 文件），默认深度 3 足够。
/// </summary>
public class BrowsePathCommandPayload
{
    /// <summary>要浏览的绝对路径。留空表示"列出本机固定磁盘"，作为浏览的起点。</summary>
    public string? Path { get; set; }

    /// <summary>向下抓取的层数（1 = 只抓直接子项）。Agent 端上限 6。</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>整棵子树最多返回的条目数，防止在几十万文件的备份盘上把自己撑死。Agent 端上限 20000。</summary>
    public int MaxEntries { get; set; } = 3000;
}

/// <summary>
/// browse_path 执行结果（Agent → 服务端，原样进 commands.result_payload）。
///
/// 只含元数据。文件内容在任何情况下都不通过这条通道传输——这是这个能力的边界，
/// 越过它就等于给管理界面开了一个远程读文件的口子。
/// </summary>
public class BrowseSnapshotDto
{
    /// <summary>本次快照的根路径。磁盘列表模式下为空串。</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>true 表示 Entries 是磁盘根列表而不是某个目录的内容。</summary>
    public bool IsDriveList { get; set; }

    /// <summary>子树。目录节点的 Children 已按同一份限额填充。</summary>
    public List<BrowseEntryDto> Entries { get; set; } = [];

    /// <summary>实际返回的条目总数（含各层）。</summary>
    public int TotalEntries { get; set; }

    /// <summary>是否因为 maxEntries 触顶而截断。截断时结构推断的结论不完整，必须如实告诉使用者。</summary>
    public bool Truncated { get; set; }

    /// <summary>本次实际使用的抓取深度。</summary>
    public int MaxDepth { get; set; }

    public DateTime CapturedAt { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>
    /// 枚举时被拒绝访问的目录。不静默吞掉：备份目录恰好是最容易设了 ACL 的地方，
    /// 「这里看起来是空的」和「这里我看不见」对使用者是完全不同的两件事。
    /// </summary>
    public List<string> DeniedPaths { get; set; } = [];

    /// <summary>
    /// 根目录本身的文件统计。子目录的同名字段挂在各自的 BrowseEntryDto 上，
    /// 根目录没有对应的条目，只能放在这里——否则「源目录下直接堆着文件」这种最简单的结构
    /// 反而拿不到准确的文件总数与总字节。
    /// </summary>
    public int? RootFileCount { get; set; }

    public long? RootTotalFileBytes { get; set; }

    public bool RootSampled { get; set; }
}

/// <summary>目录树中的一个节点（文件或目录），只有元数据。</summary>
public class BrowseEntryDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>相对快照根的路径，'/' 分隔。磁盘列表模式下等于盘符路径。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    /// <summary>文件大小；目录为 null。</summary>
    public long? SizeBytes { get; set; }

    public DateTime? LastModifiedAt { get; set; }

    /// <summary>目录的直接子项数（含未返回的部分）；文件为 null。</summary>
    public int? ChildCount { get; set; }

    /// <summary>
    /// 该目录下的文件总数（含抽样未返回的部分）；文件节点为 null。
    ///
    /// 推断端需要的是「每个叶子有几个文件、名字是什么模式」，不是每个文件的名字。
    /// 有了这个数，抽样之后 InferRequiredFiles 的「各单元里的个数是否一致」那一档
    /// 才仍然判得准——只数抓回来的那几个会把一致的判成不一致。
    /// </summary>
    public int? FileCount { get; set; }

    /// <summary>该目录下文件的总字节数（含抽样未返回的部分）；文件节点为 null。</summary>
    public long? TotalFileBytes { get; set; }

    /// <summary>
    /// 该目录的文件做过抽样（只返回了最新的一批）。
    ///
    /// 与 Truncated 分开：Truncated 是「有整块结构没看见」，Sampled 是
    /// 「看见了全貌的代表性样本，明细没全带回来」。前者要扣置信度，后者不该扣。
    /// </summary>
    public bool Sampled { get; set; }

    /// <summary>
    /// 该条目是 junction / 符号链接。枚举时**不下探**，但照常列出并标记——
    /// 静默跳过会让「这里有东西」表现成「这里是空的」。
    /// </summary>
    public bool IsReparsePoint { get; set; }

    /// <summary>该目录的子项因限额未能全部返回。</summary>
    public bool Truncated { get; set; }

    /// <summary>该目录已到达 maxDepth，未继续下探——它可能还有内容，只是这次没抓。</summary>
    public bool DepthLimited { get; set; }

    /// <summary>该目录枚举时被拒绝访问。</summary>
    public bool AccessDenied { get; set; }

    public bool IsHidden { get; set; }

    public List<BrowseEntryDto> Children { get; set; } = [];
}
