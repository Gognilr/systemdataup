using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Upload;

/// <summary>上传会话（分块上传协议核心表），对应 upload_sessions 表</summary>
public class UploadSession
{
    public Guid Id { get; set; }

    /// <summary>所属批次（可空，单独下发时为空）</summary>
    public Guid? UploadBatchId { get; set; }

    public Guid ClientId { get; set; }

    public Guid TaskId { get; set; }

    public Guid CandidateBackupSetId { get; set; }

    public UploadStatus Status { get; set; } = UploadStatus.Created;

    public int TotalFiles { get; set; }

    public long TotalBytes { get; set; }

    public long UploadedBytes { get; set; }

    public long VerifiedBytes { get; set; }

    /// <summary>分块大小（字节）</summary>
    public int ChunkSizeBytes { get; set; } = 8388608;

    /// <summary>服务端暂存路径</summary>
    public string? StagingPath { get; set; }

    public DateTime? StartedAt { get; set; }

    /// <summary>最后活跃时间（超时回收依据）</summary>
    public DateTime? LastActivityAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? VerifiedAt { get; set; }

    /// <summary>提交入仓库时间</summary>
    public DateTime? CommittedAt { get; set; }

    public int RetryCount { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>幂等键（唯一，断点续传恢复依据）</summary>
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // 导航属性
    public UploadBatch? UploadBatch { get; set; }
    public Client.Client Client { get; set; } = null!;
    public Backup.BackupTask Task { get; set; } = null!;
    public Backup.CandidateBackupSet CandidateBackupSet { get; set; } = null!;
    public ICollection<UploadFileEntity> Files { get; set; } = [];
}
