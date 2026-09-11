using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Backup;

/// <summary>
/// 备份计划（V028，对应 backup_plans 表）：把多个备份任务编成一组，到点按 SortOrder 依次驱动。
///
/// 调度此前完全在客户端（Agent 按各任务自己的 cron 触发扫描），因此跨客户端的顺序执行
/// 客户端做不到——A 机器无从知道 B 机器传完没有。计划由服务端的
/// SequentialExecutionWorker 驱动，前一项终结（成功/失败/超时）才放下一项。
/// </summary>
public class BackupPlan
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 每次到点跑完后发一条汇总回执（成功几项、失败几项、哪几项失败）。默认关闭。
    ///
    /// 计划级而不是任务级：一个 12 项的计划按任务发就是 12 条「都挺好」，
    /// 而人要问的是「昨晚那批跑完了吗，有没有翻车的」。一晚上一条就够。
    /// 打开它之后，这个计划里的任务自己那条成功回执会被压掉——以计划为准，不重复报。
    /// </summary>
    public bool NotifyOnFinish { get; set; }

    public PlanScheduleKind ScheduleKind { get; set; } = PlanScheduleKind.Daily;

    /// <summary>执行时刻（Timezone 所在时区的本地时刻）</summary>
    public TimeSpan RunAt { get; set; } = new(2, 0, 0);

    /// <summary>Weekly 时生效：ISO 周几（1=周一 … 7=周日），逗号分隔，如 "1,3,5"</summary>
    public string? DaysOfWeek { get; set; }

    public string Timezone { get; set; } = "Asia/Shanghai";

    /// <summary>并发度：1 = 严格按顺序一个一个来；N = 最多 N 个同时跑</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>单项超时（分钟）：一台关机的客户端不能把整晚的计划拖死</summary>
    public int ItemTimeoutMinutes { get; set; } = 240;

    public DateTime? LastRunAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<BackupPlanItem> Items { get; set; } = [];
}

/// <summary>备份计划的编组成员与执行顺序（对应 backup_plan_items 表）</summary>
public class BackupPlanItem
{
    public Guid Id { get; set; }

    public Guid PlanId { get; set; }

    public Guid TaskId { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public BackupPlan Plan { get; set; } = null!;

    public BackupTask Task { get; set; } = null!;
}
