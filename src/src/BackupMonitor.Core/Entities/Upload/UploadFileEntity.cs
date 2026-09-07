using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Upload;

/// <summary>上传文件（会话内单文件进度），对应 upload_files 表。
/// 类名使用 UploadFileEntity 以避免与常见类型名冲突。</summary>
public class UploadFileEntity
{
    public Guid Id { get; set; }

    public Guid UploadSessionId { get; set; }

    /// <summary>
    /// 对应预检阶段的候选文件。可空：候选清单会被下一次预检整体替换，
    /// 而已经传过的 upload_files 必须留着（它自带 relative_path / size / sha256，
    /// 不依赖这个引用）。此前这里是 NOT NULL + RESTRICT，结果是任何一个
    /// 传过一次的候选再也预检不了——删清单撞外键，接口 500，任务就此卡死。
    /// </summary>
    public Guid? CandidateFileId { get; set; }

    /// <summary>相对路径（会话内唯一）</summary>
    public string RelativePath { get; set; } = null!;

    public long SizeBytes { get; set; }

    /// <summary>客户端上报的期望 SHA-256</summary>
    public string ExpectedSha256 { get; set; } = null!;

    /// <summary>服务端接收后计算的 SHA-256（双端校验）</summary>
    public string? ServerSha256 { get; set; }

    /// <summary>
    /// 客户端在 complete 时自报的整文件 SHA-256（T2）。
    ///
    /// 只作附加交叉验证：它由上传方单方面给出，权威基准永远是 ExpectedSha256——
    /// 那是预检清单里的值，服务端持有。整文件复核挪到入库阶段之后，
    /// complete 那一刻已经没有地方比这个值了，所以先存下来。
    /// </summary>
    public string? ClientDeclaredSha256 { get; set; }

    public long UploadedBytes { get; set; }

    public int TotalChunks { get; set; }

    public int UploadedChunks { get; set; }

    public UploadFileStatus Status { get; set; } = UploadFileStatus.Pending;

    /// <summary>服务端临时文件路径</summary>
    public string? TempPath { get; set; }

    public string? ErrorCode { get; set; }

    // 导航属性
    public UploadSession UploadSession { get; set; } = null!;
    public Backup.CandidateFile? CandidateFile { get; set; }
    public ICollection<UploadChunk> Chunks { get; set; } = [];
}
