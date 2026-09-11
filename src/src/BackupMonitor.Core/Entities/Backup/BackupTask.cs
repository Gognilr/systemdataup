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

    /// <summary>
    /// 任务模式。默认自动：预检通过即下发上传，全程无人工介入。
    /// 曾默认 ApprovalRequired，但候选备份集的审批没有任何管理界面入口，
    /// 于是新建的任务默认就卡在 wait_for_approval 上永远不上传——默认值反了。
    /// </summary>
    public TaskMode TaskMode { get; set; } = TaskMode.Automatic;

    /// <summary>暂停前的任务模式（恢复时还原，V002）</summary>
    public TaskMode? PreviousTaskMode { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 这个任务备份成功后发一条回执通知。默认关闭。
    ///
    /// 按任务而不是全局，是因为这类通知的数量直接决定它的价值：每个任务每天成功一次，
    /// 几台机器就是每天几十条。收件人一旦开始习惯性划过这些「都挺好」的消息，
    /// 「备份复查未通过」也会跟着一起被划过去——报警的可信度是整体的。
    /// 只给真正要盯的那几个任务打开。
    /// </summary>
    public bool NotifyOnSuccess { get; set; }

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

    /// <summary>
    /// 单文件内同时在途的分块数（1-16，默认 4）。
    /// 性能参数，不是限速手段——限速的唯一口径是 <see cref="BandwidthLimitKbps"/>。
    /// </summary>
    public int MaxParallelChunks { get; set; } = 4;

    /// <summary>
    /// 同时在传的文件数（1-8，默认 3）。解决的是「每个文件三次串行往返」，
    /// 对几千个小文件的形态收益最大；它与 <see cref="MaxParallelChunks"/> 共用同一个
    /// 在途分块信号量，因此不会把在途分块数乘上去。
    /// </summary>
    public int MaxParallelFiles { get; set; } = 3;

    /// <summary>预检阶段同时读盘算 SHA-256 的文件数（1-8，默认 3）。</summary>
    public int MaxParallelHashes { get; set; } = 3;

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
