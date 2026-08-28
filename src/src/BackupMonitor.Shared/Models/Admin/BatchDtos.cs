using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>批量操作范围（设计书 17.1）</summary>
public class BatchScopeDto
{
    public List<Guid>? ClientGroupIds { get; set; }
    public List<Guid>? ClientIds { get; set; }
    public List<Guid>? TaskIds { get; set; }
    public List<string>? ApplicationNames { get; set; }
}

/// <summary>创建批量预检请求（设计书 17.1）</summary>
public class CreatePrecheckBatchRequest
{
    [Required]
    public BatchScopeDto Scope { get; set; } = new();

    /// <summary>仅包含启用任务</summary>
    public bool OnlyEnabled { get; set; } = true;
}

/// <summary>创建批量预检响应</summary>
public class CreatePrecheckBatchResponse
{
    /// <summary>实际下发的预检指令数</summary>
    public int DispatchedCommands { get; set; }

    /// <summary>跳过的任务数（未启用/客户端离线等）</summary>
    public int SkippedTasks { get; set; }

    public List<Guid> CommandIds { get; set; } = [];
}

/// <summary>创建批量上传请求（设计书 17.2）</summary>
public class CreateUploadBatchRequest
{
    [Required]
    [MinLength(1)]
    public List<Guid> CandidateBackupSetIds { get; set; } = [];

    public string? Name { get; set; }

    /// <summary>
    /// 同时最多几台客户端在传。V028 起这个值真的会限流：批次投进顺序执行队列，
    /// 它就是队列的并发度。此前它只是存下来给历史页显示，没有任何调度逻辑读它。
    /// </summary>
    [Range(1, 50)]
    public int MaxConcurrentClients { get; set; } = 2;

    public int? TemporaryBandwidthLimitKbps { get; set; }

    /// <summary>跳过忙碌客户端（有活动上传）</summary>
    public bool SkipBusyClients { get; set; } = true;
}

/// <summary>批量上传批次响应（设计书 17.3）</summary>
public class UploadBatchDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }

    /// <summary>pending / running / completed / partial / failed / cancelled</summary>
    public string Status { get; set; } = null!;

    public int MaxConcurrentClients { get; set; }
    public int? BandwidthLimitKbps { get; set; }
    public int TotalItems { get; set; }
    public int SucceededItems { get; set; }
    public int FailedItems { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public List<UploadBatchItemDto> Items { get; set; } = [];
}

/// <summary>批次内单项（候选 → 指令/会话状态）</summary>
public class UploadBatchItemDto
{
    public Guid CandidateBackupSetId { get; set; }
    public string CandidateKey { get; set; } = null!;
    public Guid ClientId { get; set; }
    public string ClientHostname { get; set; } = null!;
    public Guid? CommandId { get; set; }
    public string? CommandStatus { get; set; }
    public Guid? UploadSessionId { get; set; }
    public string? UploadSessionStatus { get; set; }

    /// <summary>队列里的状态：pending（排队中）/ running / succeeded / failed / timeout / skipped / cancelled</summary>
    public string? QueueStatus { get; set; }

    public string? Message { get; set; }
}
