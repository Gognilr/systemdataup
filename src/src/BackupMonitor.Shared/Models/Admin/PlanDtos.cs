using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>备份计划（V028）</summary>
public class BackupPlanDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public bool Enabled { get; set; }

    /// <inheritdoc cref="BackupPlanUpsertDto.NotifyOnFinish"/>
    public bool NotifyOnFinish { get; set; }

    /// <summary>daily / weekly</summary>
    public string ScheduleKind { get; set; } = "daily";

    /// <summary>执行时刻 HH:mm（计划时区里的本地时刻）</summary>
    public string RunAt { get; set; } = "02:00";

    /// <summary>weekly 生效：ISO 周几 1~7</summary>
    public List<int> DaysOfWeek { get; set; } = [];

    public string Timezone { get; set; } = "Asia/Shanghai";

    public int MaxConcurrent { get; set; }

    public int ItemTimeoutMinutes { get; set; }

    public DateTime? LastRunAt { get; set; }

    /// <summary>下一次应执行时刻（UTC），计划停用时为空</summary>
    public DateTime? NextRunAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<BackupPlanItemDto> Items { get; set; } = [];

    /// <summary>正在执行中的那一次（没有则为空）</summary>
    public ExecutionRunDto? ActiveRun { get; set; }
}

/// <summary>计划里的一项（一个备份任务）</summary>
public class BackupPlanItemDto
{
    public Guid TaskId { get; set; }
    public string TaskName { get; set; } = null!;
    public string ApplicationName { get; set; } = null!;
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = null!;
    public bool TaskEnabled { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>创建/更新备份计划</summary>
public class BackupPlanUpsertDto : IValidatableObject
{
    [Required(ErrorMessage = "name 必填")]
    [MaxLength(128, ErrorMessage = "name 不能超过 128 字符")]
    public string Name { get; set; } = null!;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 每次到点跑完后发一条汇总回执（成功几项、失败几项、哪几项失败）。默认关闭。
    ///
    /// 跑完就发，不只在有失败时发：只在出事时发的摘要会退化成另一种告警，
    /// 而它真正不可替代的价值恰恰是那条「12 项全成功」——收到它才知道计划确实跑了。
    /// 只对**到点触发**的执行发；人手点「立即执行」时正盯着页面看，再推一条是纯噪音。
    /// </summary>
    public bool NotifyOnFinish { get; set; }

    /// <summary>daily / weekly</summary>
    public string ScheduleKind { get; set; } = "daily";

    /// <summary>执行时刻 HH:mm</summary>
    [Required(ErrorMessage = "runAt 必填")]
    public string RunAt { get; set; } = "02:00";

    public List<int> DaysOfWeek { get; set; } = [];

    public string Timezone { get; set; } = "Asia/Shanghai";

    /// <summary>1 = 严格按顺序；N = 最多 N 个同时跑</summary>
    [Range(1, 50, ErrorMessage = "maxConcurrent 必须在 1~50 之间")]
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>单项超时（分钟）。超时即判该项失败并继续下一项，避免一台关机的机器拖死整个计划</summary>
    [Range(5, 10080, ErrorMessage = "itemTimeoutMinutes 必须在 5~10080 之间")]
    public int ItemTimeoutMinutes { get; set; } = 240;

    /// <summary>按执行顺序排列的任务 ID</summary>
    public List<Guid> TaskIds { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ScheduleKind is not ("daily" or "weekly"))
            yield return new ValidationResult("scheduleKind 只能是 daily 或 weekly", [nameof(ScheduleKind)]);

        if (!TimeSpan.TryParse(RunAt, out var runAt) || runAt < TimeSpan.Zero || runAt >= TimeSpan.FromDays(1))
            yield return new ValidationResult("runAt 必须是 HH:mm 形式的时刻", [nameof(RunAt)]);

        if (ScheduleKind == "weekly" && DaysOfWeek.Count == 0)
            yield return new ValidationResult("每周执行至少要选一天", [nameof(DaysOfWeek)]);

        if (DaysOfWeek.Any(d => d is < 1 or > 7))
            yield return new ValidationResult("daysOfWeek 只能是 1~7（1=周一，7=周日）", [nameof(DaysOfWeek)]);

        if (TaskIds.Count != TaskIds.Distinct().Count())
            yield return new ValidationResult("同一个任务不能在计划里出现两次", [nameof(TaskIds)]);
    }
}

/// <summary>一次顺序执行</summary>
public class ExecutionRunDto
{
    public Guid Id { get; set; }

    /// <summary>backup_plan / upload_batch</summary>
    public string Kind { get; set; } = null!;

    public Guid? PlanId { get; set; }
    public string? PlanName { get; set; }
    public Guid? UploadBatchId { get; set; }
    public string? Name { get; set; }

    /// <summary>pending / running / completed / partial / failed / cancelled</summary>
    public string Status { get; set; } = null!;

    public int MaxConcurrent { get; set; }
    public int ItemTimeoutMinutes { get; set; }

    /// <summary>schedule / manual</summary>
    public string TriggerSource { get; set; } = null!;

    public int TotalItems { get; set; }
    public int SucceededItems { get; set; }
    public int FailedItems { get; set; }

    public DateTime? ScheduledFor { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public List<ExecutionRunItemDto> Items { get; set; } = [];
}

/// <summary>队列里的一项</summary>
public class ExecutionRunItemDto
{
    public Guid Id { get; set; }
    public int SortOrder { get; set; }
    public Guid TaskId { get; set; }
    public string TaskName { get; set; } = null!;
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = null!;

    /// <summary>pending / running / succeeded / failed / timeout / skipped / cancelled</summary>
    public string Status { get; set; } = null!;

    public Guid? CommandId { get; set; }
    public Guid? UploadSessionId { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// 此刻的队列快照：几个在传、谁在跑、谁在等、等的是什么。
///
/// queue-status 只给数字，回答不了「什么时候轮到我」——十台服务器排在
/// 并发度 1 的计划后面时，人要看的是这份名单和它的顺序。
/// </summary>
public class ExecutionQueueSnapshotDto
{
    /// <summary>此刻占着全局上传名额的会话数</summary>
    public int ActiveUploads { get; set; }

    /// <summary>全局上传上限（max_concurrent_uploads_total）</summary>
    public int GlobalLimit { get; set; }

    /// <summary>已经开跑的项</summary>
    public int RunningItems { get; set; }

    /// <summary>在等全局上传名额的项</summary>
    public int WaitingForUploadSlot { get; set; }

    /// <summary>在等本次执行并发度的项（前一项还没跑完）</summary>
    public int WaitingInRun { get; set; }

    /// <summary>在跑的排前面，其余按「执行产生的先后 + 执行内的顺序号」——与放行顺序同一口径</summary>
    public List<ExecutionQueueEntryDto> Items { get; set; } = [];
}

/// <summary>队列名单里的一行</summary>
public class ExecutionQueueEntryDto
{
    public Guid ItemId { get; set; }

    public Guid RunId { get; set; }

    public string? RunName { get; set; }

    /// <summary>backup_plan / upload_batch / manual</summary>
    public string RunKind { get; set; } = null!;

    /// <summary>schedule / manual / auto_upload_queued</summary>
    public string TriggerSource { get; set; } = null!;

    /// <summary>这次执行的并发度：1 就是严格一个接一个</summary>
    public int RunMaxConcurrent { get; set; }

    /// <summary>这一项在本次执行里的顺序号（0 基）</summary>
    public int SortOrder { get; set; }

    public Guid TaskId { get; set; }

    public string TaskName { get; set; } = null!;

    public Guid ClientId { get; set; }

    public string ClientName { get; set; } = null!;

    /// <summary>precheck_task / upload_candidate / upload_latest：预检不占上传名额</summary>
    public string CommandType { get; set; } = null!;

    /// <summary>pending / running</summary>
    public string Status { get; set; } = null!;

    /// <summary>running / waiting_in_run / waiting_for_slot</summary>
    public string Wait { get; set; } = null!;

    public DateTime RunCreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }
}
