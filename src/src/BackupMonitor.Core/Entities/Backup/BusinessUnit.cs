namespace BackupMonitor.Core.Entities.Backup;

/// <summary>业务单元（账套、数据库等独立对象），对应 business_units 表</summary>
public class BusinessUnit
{
    public Guid Id { get; set; }

    public Guid TaskId { get; set; }

    /// <summary>外部业务键（如账套编码，任务内唯一）</summary>
    public string ExternalKey { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>相对备份源根路径的子路径</summary>
    public string? SourceRelativePath { get; set; }

    /// <summary>是否为期望存在的单元（缺失则告警）</summary>
    public bool Expected { get; set; } = true;

    public bool Ignored { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? LastDiscoveredAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // 导航属性
    public BackupTask Task { get; set; } = null!;
}
