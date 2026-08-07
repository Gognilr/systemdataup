using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>保留策略（第二批补充设计；数据表 retention_policies）</summary>
public class RetentionPolicyDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>保留最近 N 个版本</summary>
    public int? KeepLastCount { get; set; }

    /// <summary>保留周版本数</summary>
    public int? KeepWeeklyCount { get; set; }

    /// <summary>保留月末版本数</summary>
    public int? KeepMonthlyCount { get; set; }

    /// <summary>保留年度版本数</summary>
    public int? KeepYearlyCount { get; set; }

    /// <summary>最短保留天数</summary>
    public int MinimumRetentionDays { get; set; }

    /// <summary>回收区保留天数</summary>
    public int RecycleBinDays { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>引用该策略的备份任务数量</summary>
    public int BoundTaskCount { get; set; }
}

/// <summary>创建/更新保留策略请求</summary>
public class RetentionPolicyUpsertDto
{
    [Required(ErrorMessage = "name 必填")]
    [MaxLength(128, ErrorMessage = "name 不能超过 128 字符")]
    public string Name { get; set; } = null!;

    [Range(1, int.MaxValue, ErrorMessage = "keepLastCount 必须大于 0")]
    public int? KeepLastCount { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "keepWeeklyCount 必须大于 0")]
    public int? KeepWeeklyCount { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "keepMonthlyCount 必须大于 0")]
    public int? KeepMonthlyCount { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "keepYearlyCount 必须大于 0")]
    public int? KeepYearlyCount { get; set; }

    [Range(0, 36500, ErrorMessage = "minimumRetentionDays 超出范围")]
    public int MinimumRetentionDays { get; set; } = 30;

    [Range(0, 3650, ErrorMessage = "recycleBinDays 超出范围")]
    public int RecycleBinDays { get; set; } = 30;
}
