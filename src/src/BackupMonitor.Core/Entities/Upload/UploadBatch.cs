using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Upload;

/// <summary>管理员批量下发上传的批次，对应 upload_batches 表</summary>
public class UploadBatch
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    /// <summary>创建人</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>批次内最大并发客户端数</summary>
    public int MaxConcurrentClients { get; set; } = 2;


    /// <summary>批次带宽限速（KB/s）</summary>
    public int? BandwidthLimitKbps { get; set; }

    public BatchStatus Status { get; set; } = BatchStatus.Pending;

    public int TotalItems { get; set; }

    public int SucceededItems { get; set; }

    public int FailedItems { get; set; }

    /// <summary>幂等键（唯一，V002 迁移引入，对应设计书 8.5 幂等要求）</summary>
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    // 导航属性
    public Rbac.User? CreatedByUser { get; set; }
    public ICollection<UploadSession> UploadSessions { get; set; } = [];
}
