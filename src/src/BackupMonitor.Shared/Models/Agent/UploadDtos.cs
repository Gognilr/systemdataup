using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Agent;

/// <summary>创建上传会话请求（设计书 14.1）</summary>
public class CreateUploadSessionRequest
{
    [Required]
    public Guid CandidateBackupSetId { get; set; }

    /// <summary>触发上传的指令 ID</summary>
    public Guid? CommandId { get; set; }

    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>分块大小（字节，4MB-32MB）</summary>
    [Range(4 * 1024 * 1024, 32 * 1024 * 1024)]
    public int ChunkSizeBytes { get; set; } = 8388608;

    /// <summary>清单 SHA-256（与候选一致性校验）</summary>
    public string? ManifestHash { get; set; }

    /// <summary>幂等键（断点续传恢复依据）</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>创建上传会话响应（设计书 14.1）</summary>
public class CreateUploadSessionResponse
{
    public Guid UploadSessionId { get; set; }

    /// <summary>created / uploading / resumed</summary>
    public string Status { get; set; } = null!;

    public int ChunkSizeBytes { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public List<UploadSessionFileDto> Files { get; set; } = [];
}

/// <summary>会话内单文件（创建会话时返回）</summary>
public class UploadSessionFileDto
{
    public Guid UploadFileId { get; set; }
    public string RelativePath { get; set; } = null!;

    /// <summary>"all" 表示全部缺失，或给出缺失块序号列表</summary>
    public object MissingChunks { get; set; } = "all";
}

/// <summary>查询缺失块响应（设计书 14.2）</summary>
public class MissingChunksResponse
{
    /// <summary>缺失块序号列表（块少时返回）</summary>
    public List<int>? Missing { get; set; }

    /// <summary>已接收块序号列表</summary>
    public List<int>? Received { get; set; }

    /// <summary>缺失块范围（块特别多时返回）</summary>
    public List<ChunkRangeDto>? MissingRanges { get; set; }
}

/// <summary>块序号范围（含首尾）</summary>
public class ChunkRangeDto
{
    public int Start { get; set; }
    public int End { get; set; }
}

/// <summary>上传分块响应（设计书 14.3）</summary>
public class UploadChunkResponse
{
    public bool Received { get; set; }
    public int ChunkIndex { get; set; }

    /// <summary>服务端计算的块 SHA-256</summary>
    public string ServerHash { get; set; } = null!;
}

/// <summary>完成单文件请求（设计书 14.4）</summary>
public class CompleteUploadFileRequest
{
    public long SizeBytes { get; set; }

    [Required]
    public string Sha256 { get; set; } = null!;
}

/// <summary>完成单文件响应</summary>
public class CompleteUploadFileResponse
{
    public Guid UploadFileId { get; set; }

    /// <summary>received / verified / failed</summary>
    public string Status { get; set; } = null!;

    /// <summary>服务端计算的文件 SHA-256</summary>
    public string? ServerSha256 { get; set; }
}

/// <summary>
/// 中断会话请求（待办方案 E）：这次传失败了，但会话与暂存要留着，下次接着传。
/// 与 cancel 的区别就在这里——cancel 是「这次不传了」，interrupt 是「等下再来」。
/// </summary>
public class InterruptUploadSessionRequest
{
    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>完成会话请求（设计书 14.5）</summary>
public class CompleteUploadSessionRequest
{
    public string? ManifestHash { get; set; }
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>完成会话响应（设计书 14.5）</summary>
public class CompleteUploadSessionResponse
{
    /// <summary>verifying / committed</summary>
    public string Status { get; set; } = null!;

    /// <summary>校验操作 ID（异步查询用）</summary>
    public Guid VerificationOperationId { get; set; }
}

/// <summary>查询会话响应（设计书 14.6）</summary>
public class UploadSessionStatusResponse
{
    public Guid UploadSessionId { get; set; }

    /// <summary>会话状态（snake_case）</summary>
    public string Status { get; set; } = null!;

    public long UploadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }

    public List<UploadFileProgressDto> Files { get; set; } = [];

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>是否可断点续传</summary>
    public bool Resumable { get; set; }
}

/// <summary>会话内文件进度</summary>
public class UploadFileProgressDto
{
    public Guid UploadFileId { get; set; }
    public string RelativePath { get; set; } = null!;

    /// <summary>pending / uploading / received / verified / failed</summary>
    public string Status { get; set; } = null!;

    public long UploadedBytes { get; set; }
    public long SizeBytes { get; set; }
    public int UploadedChunks { get; set; }
    public int TotalChunks { get; set; }

    /// <summary>缺失块（块数可控时返回）</summary>
    public List<int>? MissingChunks { get; set; }
}
