using System.ComponentModel.DataAnnotations;
using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>浏览客户端目录（管理端 → 服务端）。</summary>
public class BrowseClientPathRequest
{
    /// <summary>要浏览的绝对路径。留空表示列出该客户端的固定磁盘。</summary>
    public string? Path { get; set; }

    [Range(1, 6)]
    public int MaxDepth { get; set; } = 3;

    [Range(50, 20000)]
    public int MaxEntries { get; set; } = 3000;
}

/// <summary>
/// 浏览结果轮询响应。
///
/// 指令是异步的（Agent 每 10 秒领一次），所以这个接口既可能返回"还在等"，
/// 也可能返回快照本身。Status 走指令状态：pending / claimed / running / succeeded / failed / expired。
/// </summary>
public class BrowseClientPathResultDto
{
    public Guid CommandId { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultCode { get; set; }
    public string? ResultMessage { get; set; }

    /// <summary>指令成功时的目录快照；未完成或失败时为 null。</summary>
    public BrowseSnapshotDto? Snapshot { get; set; }
}

/// <summary>基于某次快照做结构推断 / 规则预演的共同入参。</summary>
public class SnapshotScopedRequest
{
    /// <summary>快照所属的 browse_path 指令。</summary>
    [Required]
    public Guid CommandId { get; set; }

    /// <summary>
    /// 在快照内选定的源路径（绝对路径或相对快照根的路径）。
    /// 留空表示直接用快照根。
    /// </summary>
    public string? Path { get; set; }
}

/// <summary>结构推断入参。</summary>
public class InferRecognizerRequest : SnapshotScopedRequest
{
}

/// <summary>规则预演入参：拿一条具体的规则，在快照上跑一遍看会判成什么。</summary>
public class PreviewRecognizerRequest : SnapshotScopedRequest
{
    [Required]
    public string RecognizerType { get; set; } = null!;

    /// <summary>识别规则 JSON。</summary>
    public string? RecognizerConfig { get; set; }

    /// <summary>
    /// 稳定观察窗口（秒）。预演时用来复现"最新文件还在写"这条判定，
    /// 传 0 表示不检查。默认取任务默认值 600。
    /// </summary>
    [Range(0, 86400)]
    public int StabilityIntervalSeconds { get; set; } = 600;
}

/// <summary>
/// 推断出来的识别方案。
///
/// Summary 是给人看的一句话，Evidence 是它凭什么这么说。两者缺一不可：
/// 只给结论，人无法判断对错，那这个"确认"就退化成了盲点头。
/// </summary>
public class RecognizerProposalDto
{
    public string SourcePath { get; set; } = string.Empty;

    public string RecognizerType { get; set; } = null!;

    /// <summary>识别规则 JSON（已格式化）。</summary>
    public string RecognizerConfig { get; set; } = "{}";

    /// <summary>0–100。低于 50 时界面应当退回模板选择，而不是假装看懂了。</summary>
    public int Confidence { get; set; }

    /// <summary>一句中文描述，例如「下面有 9 个账套，每个账套按日期建目录……」。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>推断依据，逐条列出观察到的现象。</summary>
    public List<string> Evidence { get; set; } = [];

    /// <summary>推断出的必需文件候选，界面用它渲染勾选框。</summary>
    public List<RequiredFileCandidateDto> RequiredFileCandidates { get; set; } = [];

    /// <summary>推荐的应用名称（从文件特征猜，例如出现 UFDATA.BAK 时给 "用友 U8"）。</summary>
    public string? SuggestedApplicationName { get; set; }

    /// <summary>
    /// 这次没看懂，而且原因很可能是快照抓得不够深——界面应当再抓深一点重试一次。
    ///
    /// 存在的理由：向导默认只抓 4 层，因为层数一深条目数是乘出来的，很容易顶到
    /// 3000 条上限，反而让本来看得懂的结构变得不可靠。所以深度不能一律调大，
    /// 而要在**确实被深度卡住时**才多花这一次往返。
    ///
    /// 不做这件事的话，表现是向导对一个只差一层就能看懂的结构回一句「没能看懂」，
    /// 而人在资源管理器里明明看得见东西。
    /// </summary>
    public bool NeedsDeeperScan { get; set; }

    /// <summary>按这条规则在快照上跑出来的结果。</summary>
    public RecognizerPreviewDto? Preview { get; set; }
}

/// <summary>必需文件候选：这个文件在多少个备份目录里出现过。</summary>
public class RequiredFileCandidateDto
{
    public string Pattern { get; set; } = null!;

    /// <summary>
    /// `exact`：每次的文件名都一样（UFDATA.BAK）。
    /// `pattern`：文件名每次不同，但归一化后的模式稳定（UFFile_*.dat）。
    /// 界面据此说明这一条是怎么认出来的——不说清楚，人无从判断该不该勾。
    /// </summary>
    public string Kind { get; set; } = "exact";

    /// <summary>出现在多少个备份目录里。</summary>
    public int PresentIn { get; set; }

    /// <summary>一共考察了多少个备份目录。</summary>
    public int TotalUnits { get; set; }

    /// <summary>单个备份目录里，命中这条模式的文件最少有几个。</summary>
    public int CountPerUnitMin { get; set; } = 1;

    /// <summary>单个备份目录里，命中这条模式的文件最多有几个。</summary>
    public int CountPerUnitMax { get; set; } = 1;

    /// <summary>是否默认勾选。</summary>
    public bool Recommended { get; set; }

    /// <summary>
    /// 没有默认勾选的原因，直接显示给人看。
    ///
    /// 「没勾」和「为什么没勾」是两件事：只把勾去掉，使用者只会觉得系统漏了；
    /// 说清楚「各账套启用年度不同，个数本来就不一样，不能当判据」，他才能自己决定要不要勾上。
    /// </summary>
    public string? NotRecommendedReason { get; set; }

    public long TypicalSizeBytes { get; set; }
}

/// <summary>
/// 规则在快照上的预演结果。
///
/// 它跟"识别测试"的区别：识别测试要下发指令、等 Agent 真扫一遍（十几秒起）；
/// 预演跑在服务端已有的快照上（毫秒级）。改一个勾选立刻能看到判定变化，
/// 这是把配置从"填了再说"变成"看着调"的关键。
/// </summary>
public class RecognizerPreviewDto
{
    public string SourcePath { get; set; } = string.Empty;
    public string RecognizerType { get; set; } = null!;

    public List<RecognizerPreviewUnitDto> Units { get; set; } = [];

    public int PassedCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>
    /// 快照被截断或存在无权限目录时置位：预演结论只覆盖看得见的那部分，
    /// 界面必须原样转达，不能让人以为这就是全部。
    /// </summary>
    public bool SnapshotTruncated { get; set; }
    public bool SnapshotDepthLimited { get; set; }
    public List<string> DeniedPaths { get; set; } = [];

    /// <summary>快照采集时间。预演基于快照，不是"此刻"的磁盘状态。</summary>
    public DateTime CapturedAt { get; set; }
}

/// <summary>预演中的一个业务单元。</summary>
public class RecognizerPreviewUnitDto
{
    /// <summary>业务单元名（子目录单元模式下才有）。</summary>
    public string? BusinessUnit { get; set; }

    /// <summary>实际采集的目录（相对源路径）。</summary>
    public string SourceRoot { get; set; } = string.Empty;

    /// <summary>passed / required_file_missing / no_new_backup / still_changing</summary>
    public string Status { get; set; } = null!;

    public string? FailureMessage { get; set; }

    /// <summary>缺失的必需文件（Status = required_file_missing 时）。</summary>
    public List<string> MissingRequired { get; set; } = [];

    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public DateTime? NewestFileAt { get; set; }

    public List<RecognizerPreviewFileDto> Files { get; set; } = [];

    /// <summary>该单元下有文件因深度限制没抓到，判定可能不完整。</summary>
    public bool Incomplete { get; set; }
}

public class RecognizerPreviewFileDto
{
    public string RelativePath { get; set; } = null!;
    public long SizeBytes { get; set; }
    public DateTime LastModifiedAt { get; set; }
    public bool IsRequired { get; set; }
}
