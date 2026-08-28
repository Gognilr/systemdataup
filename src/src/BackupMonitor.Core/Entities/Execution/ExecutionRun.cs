using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Execution;

/// <summary>
/// 一次顺序执行（V028，对应 execution_runs 表）：备份计划到点产生一次，
/// 一次批量上传也产生一次。两者共用同一个队列与同一个执行器，
/// MaxConcurrent 就是那道一直缺席的并发闸。
/// </summary>
public class ExecutionRun
{
    public Guid Id { get; set; }

    public ExecutionRunKind Kind { get; set; }

    public Guid? PlanId { get; set; }

    public Guid? UploadBatchId { get; set; }

    public string? Name { get; set; }

    public BatchStatus Status { get; set; } = BatchStatus.Pending;

    public int MaxConcurrent { get; set; } = 1;

    public int ItemTimeoutMinutes { get; set; } = 240;

    /// <summary>schedule（计划到点）/ manual（人点了立即执行）</summary>
    public string TriggerSource { get; set; } = "schedule";

    public int TotalItems { get; set; }

    public int SucceededItems { get; set; }

    public int FailedItems { get; set; }

    public Guid? TriggeredBy { get; set; }

    /// <summary>计划的这一次「应执行时刻」。同一时刻只允许产生一次 run（唯一索引兜底）</summary>
    public DateTime? ScheduledFor { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public Backup.BackupPlan? Plan { get; set; }

    public ICollection<ExecutionRunItem> Items { get; set; } = [];
}

/// <summary>队列里的一项（对应 execution_run_items 表）：一个待驱动的任务，或一个待上传的候选备份集</summary>
public class ExecutionRunItem
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public int SortOrder { get; set; }

    public Guid ClientId { get; set; }

    public Guid TaskId { get; set; }

    public Guid? CandidateBackupSetId { get; set; }

    /// <summary>PrecheckTask（计划驱动任务）或 UploadCandidate（批量上传候选）</summary>
    public CommandType CommandType { get; set; }

    public ExecutionItemStatus Status { get; set; } = ExecutionItemStatus.Pending;

    public Guid? CommandId { get; set; }

    public Guid? UploadSessionId { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? Message { get; set; }

    public ExecutionRun Run { get; set; } = null!;

    public Backup.BackupTask Task { get; set; } = null!;
}
