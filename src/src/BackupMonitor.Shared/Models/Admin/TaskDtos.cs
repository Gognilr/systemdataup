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

    /// <summary>automatic / approval_required / manual / monitor_only</summary>
    public string TaskMode { get; set; } = "approval_required";

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

    public string TaskMode { get; set; } = "approval_required";
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
}

/// <summary>任务列表项</summary>
public class BackupTaskListItemDto
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string ClientHostname { get; set; } = null!;
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
public class DispatchCommandResponse
{
    public Guid CommandId { get; set; }

    /// <summary>pending</summary>
    public string Status { get; set; } = "pending";
}
