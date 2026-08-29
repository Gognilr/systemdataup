using System.ComponentModel.DataAnnotations;
using BackupMonitor.Shared.Models;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>创建备份任务请求（设计书 16.1）</summary>
public class CreateBackupTaskRequest
{
    [Required]
    public Guid ClientId { get; set; }

    [Required]
    [StringLength(255)]
    public string Name { get; set; } = null!;

    [Required]
    public string ApplicationName { get; set; } = null!;

    [Required]
    public string SourcePath { get; set; } = null!;

    /// <summary>latest_single_file / latest_directory / multi_file_set / subdirectory_units</summary>
    [Required]
    public string RecognizerType { get; set; } = null!;

    /// <summary>automatic / approval_required / manual / monitor_only（默认 automatic：扫到即传，不需要人工干预）</summary>
    public string TaskMode { get; set; } = "automatic";

    public bool Enabled { get; set; } = true;

    public int Priority { get; set; } = 100;

    /// <summary>low / normal / high / critical</summary>
    public string ImportanceLevel { get; set; } = "normal";

    public string? ScanSchedule { get; set; }
    public string? UploadWindowStart { get; set; }
    public string? UploadWindowEnd { get; set; }
    public string ScheduleTimezone { get; set; } = "Asia/Shanghai";
    public int RandomDelayMinutes { get; set; }

    [Range(60, int.MaxValue)]
    public int StabilityIntervalSeconds { get; set; } = 600;

    public int MaxStabilityWaitSeconds { get; set; } = 7200;

    public long? MinTotalBytes { get; set; }
    public long? MaxTotalBytes { get; set; }
    public int? MinFileCount { get; set; }
    public int? BandwidthLimitKbps { get; set; }
    public int ChunkSizeBytes { get; set; } = 8388608;
    public int RetryCount { get; set; } = 3;
    public int RetryIntervalSeconds { get; set; } = 1800;
    public Guid? RetentionPolicyId { get; set; }

    /// <summary>识别规则配置（jsonb 原样存储）</summary>
    public string RecognizerConfig { get; set; } = "{}";

    public string? AlertConfig { get; set; }
}

/// <summary>修改备份任务请求（设计书 16.2，必须携带 rowVersion）</summary>
public class UpdateBackupTaskRequest
{
    [Required]
    public string Name { get; set; } = null!;

    [Required]
    public string ApplicationName { get; set; } = null!;

    [Required]
    public string SourcePath { get; set; } = null!;

    [Required]
    public string RecognizerType { get; set; } = null!;

    public string TaskMode { get; set; } = "automatic";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string ImportanceLevel { get; set; } = "normal";
    public string? ScanSchedule { get; set; }
    public string? UploadWindowStart { get; set; }
    public string? UploadWindowEnd { get; set; }
    public string ScheduleTimezone { get; set; } = "Asia/Shanghai";
    public int RandomDelayMinutes { get; set; }
    public int StabilityIntervalSeconds { get; set; } = 600;
    public int MaxStabilityWaitSeconds { get; set; } = 7200;
    public long? MinTotalBytes { get; set; }
    public long? MaxTotalBytes { get; set; }
    public int? MinFileCount { get; set; }
    public int? BandwidthLimitKbps { get; set; }
    public int ChunkSizeBytes { get; set; } = 8388608;
    public int RetryCount { get; set; } = 3;
    public int RetryIntervalSeconds { get; set; } = 1800;
    public Guid? RetentionPolicyId { get; set; }
    public string RecognizerConfig { get; set; } = "{}";
    public string? AlertConfig { get; set; }

    /// <summary>乐观锁版本号（并发修改保护，设计书 16.2）</summary>
    [Required]
    public long? RowVersion { get; set; }
}

/// <summary>任务列表查询参数（设计书 16.4）</summary>
public class BackupTaskQuery : PagedQuery
{
    public Guid? ClientId { get; set; }
    public Guid? GroupId { get; set; }

    /// <summary>automatic / approval_required / manual / monitor_only / paused</summary>
    public string? TaskMode { get; set; }

    public bool? Enabled { get; set; }
    public string? ApplicationName { get; set; }
    public bool? HasAlert { get; set; }
    public DateTime? LastSuccessBefore { get; set; }

    /// <summary>
    /// 按最近预检状态过滤，逗号分隔多值（如 path_not_found,required_file_missing）。
    /// B5：待办页此前靠 pageSize=200 拉全量再在浏览器过滤，超过 200 个任务时静默漏项；
    /// 加这个参数让待办只拉真正需要的行，天花板问题自然消失。
    /// </summary>
    public string? PrecheckStatus { get; set; }

    /// <summary>
    /// 只看"算有问题"的任务：最近预检非正常态，或已启用但超过一天没扫描且超过三天没成功。
    /// 判定逻辑与 ReportService.GetTodoSummaryAsync 共用（BackupTaskService.IsProblematic）。
    /// </summary>
    public bool? OnlyProblematic { get; set; }
}

/// <summary>任务列表项</summary>
public class BackupTaskListItemDto
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string ClientHostname { get; set; } = null!;

    /// <summary>装机时人填的名字；界面以它为主，主机名只用来消歧。</summary>
    public string ClientDisplayName { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string ApplicationName { get; set; } = null!;
    public string SourcePath { get; set; } = null!;
    public string RecognizerType { get; set; } = null!;
    public string TaskMode { get; set; } = null!;
    public bool Enabled { get; set; }
    public string ImportanceLevel { get; set; } = null!;
    public string? LastPrecheckStatus { get; set; }
    public DateTime? LastScanAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public long ConfigVersion { get; set; }
    public int ActiveAlertCount { get; set; }
}

/// <summary>任务详情</summary>
public class BackupTaskDetailDto : BackupTaskListItemDto
{
    /// <summary>暂停前的任务模式。界面据此在「恢复」时显示会回到哪个模式，null 表示未暂停过</summary>
    public string? PreviousTaskMode { get; set; }

    public Guid? TemplateId { get; set; }
    public int Priority { get; set; }
    public string? ScanSchedule { get; set; }
    public string? UploadWindowStart { get; set; }
    public string? UploadWindowEnd { get; set; }
    public string ScheduleTimezone { get; set; } = null!;
    public int RandomDelayMinutes { get; set; }
    public int StabilityIntervalSeconds { get; set; }
    public int MaxStabilityWaitSeconds { get; set; }
    public long? MinTotalBytes { get; set; }
    public long? MaxTotalBytes { get; set; }
    public int? MinFileCount { get; set; }
    public int? BandwidthLimitKbps { get; set; }
    public int ChunkSizeBytes { get; set; }
    public int RetryCount { get; set; }
    public int RetryIntervalSeconds { get; set; }
    public Guid? RetentionPolicyId { get; set; }
    public string? RetentionPolicyName { get; set; }

    /// <summary>
    /// 该任务所属的备份计划（V028）。非空时它的扫描计划不再下发给客户端，
    /// 改由计划按顺序驱动——界面据此把「扫描计划」置灰并说明原因。
    /// </summary>
    public Guid? PlanId { get; set; }
    public string? PlanName { get; set; }

    public string RecognizerConfig { get; set; } = "{}";
    public string? AlertConfig { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}

/// <summary>下发上传请求（设计书 16.7）</summary>
public class DispatchUploadRequest
{
    [Required]
    public Guid CandidateBackupSetId { get; set; }

    /// <summary>强制下发（忽略忙碌/窗口限制）</summary>
    public bool Force { get; set; }

    public int? BandwidthLimitKbps { get; set; }
}

/// <summary>下发指令响应（预检/上传通用）</summary>
/// <summary>批量「立即备份」（D3）：一次请求带上所有选中的任务</summary>
public class BatchBackupNowRequest
{
    public List<Guid> TaskIds { get; set; } = [];
}

// 这里刻意没有 forceFullHash。批量走的是执行队列，指令由 SequentialExecutionWorker
// 在放行那一刻才生成，而队列项上没有存放指令参数的地方——加一个字段只为了透传一个
// 极少用到的开关不划算。「强制完整校验」是单个任务的逃生门，对单条走 DispatchPrecheckAsync
// 的路径有效；批量要用它就一个一个点。宁可它在批量里不存在，也不要它在批量里被静默忽略。

/// <summary>批量「立即备份」的结果</summary>
public class BatchBackupNowResponse
{
    /// <summary>建出来的执行记录；退化成逐个下发时为 null</summary>
    public Guid? ExecutionRunId { get; set; }

    /// <summary>已排队的任务数</summary>
    public int QueuedTasks { get; set; }

    /// <summary>被跳过的任务数（停用 / 暂停 / 客户端不可用）</summary>
    public int SkippedTasks { get; set; }

    /// <summary>这次执行同时放行几项</summary>
    public int MaxConcurrent { get; set; }
}

public class DispatchCommandResponse
{
    public Guid CommandId { get; set; }

    /// <summary>
    /// accepted：新下发（或上一条已终结、这次复位重下）；
    /// already_running：同一个任务已经有一条同类指令正在跑，返回的是它，界面应当接着轮询这一条。
    /// </summary>
    public string Status { get; set; } = "accepted";
}

/// <summary>
/// 指令执行结果查询。识别测试是异步的：下发一条指令给 Agent，
/// 界面需要能轮询它跑完没有、跑出了什么。
/// </summary>
public class CommandResultDto
{
    public Guid CommandId { get; set; }
    public string CommandType { get; set; } = null!;
    public string Status { get; set; } = null!;
    public Guid? TaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultCode { get; set; }
    public string? ResultMessage { get; set; }

    /// <summary>指令返回的结构化结果（jsonb 原样返回）。</summary>
    public string? ResultPayload { get; set; }
}
