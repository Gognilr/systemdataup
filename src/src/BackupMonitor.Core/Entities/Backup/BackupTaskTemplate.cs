using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Backup;

/// <summary>备份任务模板（可快速创建任务），对应 backup_task_templates 表</summary>
public class BackupTaskTemplate
{
    public Guid Id { get; set; }

    /// <summary>模板编码（唯一，如 tpl_latest_file）</summary>
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public RecognizerType RecognizerType { get; set; }

    /// <summary>默认识别规则配置（jsonb）</summary>
    public string? DefaultConfig { get; set; }

    /// <summary>是否为系统内置模板（不可删除）</summary>
    public bool IsSystem { get; set; }

    /// <summary>模板版本号</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // 导航属性
    public ICollection<BackupTask> BackupTasks { get; set; } = [];
}
