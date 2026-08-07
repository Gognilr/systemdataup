namespace BackupMonitor.Core.Entities.Retention;

/// <summary>备份保留策略，对应 retention_policies 表</summary>
public class RetentionPolicy
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
    public int MinimumRetentionDays { get; set; } = 30;

    /// <summary>回收区保留天数</summary>
    public int RecycleBinDays { get; set; } = 30;

    /// <summary>扩展配置（jsonb）</summary>
    public string? Config { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // 导航属性
    public ICollection<Backup.BackupTask> BackupTasks { get; set; } = [];
}
