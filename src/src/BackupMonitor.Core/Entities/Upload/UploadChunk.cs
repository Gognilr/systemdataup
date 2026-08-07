using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Upload;

/// <summary>上传分块记录（入库后可清理明细），对应 upload_chunks 表</summary>
public class UploadChunk
{
    public Guid Id { get; set; }

    public Guid UploadFileId { get; set; }

    /// <summary>分块序号（文件内唯一）</summary>
    public int ChunkIndex { get; set; }

    public long OffsetBytes { get; set; }

    public int SizeBytes { get; set; }

    /// <summary>客户端上报的分块哈希</summary>
    public string ExpectedHash { get; set; } = null!;

    /// <summary>服务端计算的分块哈希</summary>
    public string? ServerHash { get; set; }

    public UploadChunkStatus Status { get; set; } = UploadChunkStatus.Pending;

    public DateTime? ReceivedAt { get; set; }

    // 导航属性
    public UploadFileEntity UploadFile { get; set; } = null!;
}
