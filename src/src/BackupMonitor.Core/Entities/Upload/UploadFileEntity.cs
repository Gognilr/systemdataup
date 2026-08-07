using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Upload;

/// <summary>上传文件（会话内单文件进度），对应 upload_files 表。
/// 类名使用 UploadFileEntity 以避免与常见类型名冲突。</summary>
public class UploadFileEntity
{
    public Guid Id { get; set; }

    public Guid UploadSessionId { get; set; }

    /// <summary>对应预检阶段的候选文件</summary>
    public Guid CandidateFileId { get; set; }

    /// <summary>相对路径（会话内唯一）</summary>
    public string RelativePath { get; set; } = null!;

    public long SizeBytes { get; set; }

    /// <summary>客户端上报的期望 SHA-256</summary>
    public string ExpectedSha256 { get; set; } = null!;

    /// <summary>服务端接收后计算的 SHA-256（双端校验）</summary>
    public string? ServerSha256 { get; set; }

    public long UploadedBytes { get; set; }

    public int TotalChunks { get; set; }

    public int UploadedChunks { get; set; }

    public UploadFileStatus Status { get; set; } = UploadFileStatus.Pending;

    /// <summary>服务端临时文件路径</summary>
    public string? TempPath { get; set; }

    public string? ErrorCode { get; set; }

    // 导航属性
    public UploadSession UploadSession { get; set; } = null!;
    public Backup.CandidateFile CandidateFile { get; set; } = null!;
    public ICollection<UploadChunk> Chunks { get; set; } = [];
}
