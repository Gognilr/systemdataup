using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>Agent 升级请求（第二批补充设计：经 upgrade_agent 指令通道下发）</summary>
public class UpgradeAgentRequestDto
{
    [Required(ErrorMessage = "clientIds 必填")]
    [MinLength(1, ErrorMessage = "至少需要一台客户端")]
    public List<Guid> ClientIds { get; set; } = [];

    [Required(ErrorMessage = "targetVersion 必填")]
    [MaxLength(32, ErrorMessage = "targetVersion 不能超过 32 字符")]
    public string TargetVersion { get; set; } = null!;

    /// <summary>升级包 HTTP(S) 地址；生产环境必须使用 HTTPS。</summary>
    [Required(ErrorMessage = "packageUrl 必填")]
    [Url(ErrorMessage = "packageUrl 必须是有效的 HTTP(S) 地址")]
    [MaxLength(2048, ErrorMessage = "packageUrl 不能超过 2048 字符")]
    public string PackageUrl { get; set; } = null!;

    /// <summary>升级包 SHA-256（64 位十六进制），Agent 强制校验</summary>
    [Required(ErrorMessage = "packageSha256 必填")]
    [RegularExpression("^[0-9a-fA-F]{64}$", ErrorMessage = "packageSha256 必须为 64 位十六进制")]
    public string PackageSha256 { get; set; } = null!;

    [MaxLength(500, ErrorMessage = "note 不能超过 500 字符")]
    public string? Note { get; set; }

    /// <summary>
    /// 分批计划（R20）：每批台数，逗号分隔，末位 0 表示「剩下的全放」。
    /// 留空取默认的 <c>1,5,0</c>——先 1 台，等它心跳回来且版本号变了再放 5 台，再铺开。
    ///
    /// 为什么默认要分批：一个坏包同时推给 29 台就是 29 次上门。
    /// 升级失败的机器连不上服务端，连修复指令都收不到。
    /// </summary>
    [MaxLength(64, ErrorMessage = "batchPlan 不能超过 64 字符")]
    [RegularExpression(@"^\s*\d+(\s*,\s*\d+)*\s*$", ErrorMessage = "batchPlan 必须是逗号分隔的数字，例如 1,5,0")]
    public string? BatchPlan { get; set; }
}

/// <summary>单台客户端下发结果（批量接口返回逐项结果，设计书 2.2）</summary>
public class UpgradeClientResultDto
{
    public Guid ClientId { get; set; }

    public string? Hostname { get; set; }

    public bool Success { get; set; }

    public Guid? CommandId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>Agent 升级下发响应</summary>
public class UpgradeAgentResponseDto
{
    public int Dispatched { get; set; }

    public int Failed { get; set; }

    public List<UpgradeClientResultDto> Items { get; set; } = [];
}

/// <summary>
/// 一次分批升级下发的概要（R20）。
///
/// 与旧的 <see cref="UpgradeAgentResponseDto"/> 的区别是它有生命周期：
/// 下发只是开始，成功要等客户端心跳回来并自报新版本号。
/// </summary>
public class AgentUpgradeSummaryDto
{
    public Guid Id { get; set; }

    public string TargetVersion { get; set; } = null!;

    public string PackageUrl { get; set; } = null!;

    public string? Note { get; set; }

    /// <summary>pending / running / succeeded / failed / cancelled</summary>
    public string Status { get; set; } = null!;

    public string BatchPlan { get; set; } = null!;

    public int CurrentBatch { get; set; }

    public int TotalCount { get; set; }

    public int SucceededCount { get; set; }

    public int FailedCount { get; set; }

    public int PendingCount { get; set; }

    public string? CreatedByName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? FailureReason { get; set; }
}

/// <summary>升级下发详情：逐机器逐批次的结果（R20）。</summary>
public class AgentUpgradeDetailDto : AgentUpgradeSummaryDto
{
    public List<AgentUpgradeTargetDto> Targets { get; set; } = [];
}

/// <summary>升级下发里的一台机器（R20）。</summary>
public class AgentUpgradeTargetDto
{
    public Guid ClientId { get; set; }

    public string? Hostname { get; set; }

    public string? DisplayName { get; set; }

    public int BatchIndex { get; set; }

    /// <summary>waiting / dispatched / succeeded / failed / cancelled</summary>
    public string Status { get; set; } = null!;

    public Guid? CommandId { get; set; }

    public string? VersionBefore { get; set; }

    public string? ReportedVersion { get; set; }

    public DateTime? DispatchedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 下发表单的自动填充信息（R20）。
///
/// 手抄一个 64 位十六进制哈希本身就是个故障源：抄错一位的表现是
/// 全网每一台都下载成功、校验失败，而那时人只会怀疑包坏了。
/// </summary>
public class AgentUpgradePackageInfoDto
{
    /// <summary>服务端随附的 Agent 版本号；读不到时为空。</summary>
    public string? Version { get; set; }

    /// <summary>升级包下载地址（服务端自己的 /downloads 路径）。</summary>
    public string? PackageUrl { get; set; }

    /// <summary>升级包 SHA-256；由 build-turnkey.ps1 随包写出，读不到时为空。</summary>
    public string? PackageSha256 { get; set; }

    /// <summary>读不到版本号或哈希时的原因，直接显示给管理员。</summary>
    public string? Unavailable { get; set; }
}
