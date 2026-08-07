using BackupMonitor.Core.Abstractions;
using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Backup;

/// <summary>备份任务配置（核心业务表），对应 backup_tasks 表</summary>
public class BackupTask : IHasRowVersion
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>来源模板</summary>
    public Guid? TemplateId { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>被备份的应用名称（如 用友U8、SQLServer）</summary>
    public string ApplicationName { get; set; } = null!;

    /// <summary>备份源根路径</summary>
    public string SourcePath { get; set; } = null!;

    public RecognizerType RecognizerType { get; set; }

    public TaskMode TaskMode { get; set; } = TaskMode.ApprovalRequired;

    /// <summary>暂停前的任务模式（恢复时还原，V002）</summary>
    public TaskMode? PreviousTaskMode { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>调度优先级（1-1000，越小越优先）</summary>
    public int Priority { get; set; } = 100;

    /// <summary>业务重要等级</summary>
    public ImportanceLevel ImportanceLevel { get; set; } = ImportanceLevel.Normal;

    /// <summary>扫描调度表达式（cron）</summary>
    public string? ScanSchedule { get; set; }

    /// <summary>上传窗口开始时间</summary>
    public TimeSpan? UploadWindowStart { get; set; }

    /// <summary>上传窗口结束时间</summary>
    public TimeSpan? UploadWindowEnd { get; set; }

    /// <summary>调度时区（IANA 名称）</summary>
    public string ScheduleTimezone { get; set; } = "Asia/Shanghai";

    /// <summary>随机延迟分钟数（削峰）</summary>
    public int RandomDelayMinutes { get; set; }

    /// <summary>稳定性判定间隔（秒，>=60）</summary>
    public int StabilityIntervalSeconds { get; set; } = 600;

    /// <summary>稳定等待上限（秒）</summary>
    public int MaxStabilityWaitSeconds { get; set; } = 7200;

    /// <summary>总大小下限（字节，低于则异常）</summary>
    public long? MinTotalBytes { get; set; }

    /// <summary>总大小上限（字节，超过则异常）</summary>
    public long? MaxTotalBytes { get; set; }

    /// <summary>文件数下限</summary>
    public int? MinFileCount { get; set; }

    /// <summary>带宽限速（KB/s）</summary>
    public int? BandwidthLimitKbps { get; set; }

    /// <summary>分块大小（字节，4MB-32MB，默认 8MB）</summary>
    public int ChunkSizeBytes { get; set; } = 8388608;

    /// <summary>失败重试次数</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>重试间隔（秒）</summary>
    public int RetryIntervalSeconds { get; set; } = 1800;

    /// <summary>保留策略</summary>
    public Guid? RetentionPolicyId { get; set; }

    /// <summary>配置版本号（增量下发依据）</summary>
    public long ConfigVersion { get; set; } = 1;

    /// <summary>识别规则配置（jsonb，结构因 recognizer_type 而异）</summary>
    public string RecognizerConfig { get; set; } = "{}";

    /// <summary>告警规则配置（jsonb）</summary>
    public string? AlertConfig { get; set; }

    public DateTime? LastScanAt { get; set; }

    /// <summary>最近一次预检状态</summary>
    public PrecheckStatus? LastPrecheckStatus { get; set; }

    /// <summary>最近一次成功上传时间</summary>
    public DateTime? LastSuccessAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>乐观锁版本号</summary>
    public long RowVersion { get; set; }

    // 导航属性
    public Client.Client Client { get; set; } = null!;
    public BackupTaskTemplate? Template { get; set; }
    public Retention.RetentionPolicy? RetentionPolicy { get; set; }
    public ICollection<BusinessUnit> BusinessUnits { get; set; } = [];
    public ICollection<CandidateBackupSet> CandidateBackupSets { get; set; } = [];
}
