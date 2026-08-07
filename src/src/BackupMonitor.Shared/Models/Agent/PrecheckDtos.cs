using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Agent;

/// <summary>提交预检结果请求（设计书 13.1）</summary>
public class SubmitPrecheckResultRequest
{
    /// <summary>触发本次预检的指令 ID</summary>
    public Guid? CommandId { get; set; }

    /// <summary>业务单元（subdirectory_units 类型任务）</summary>
    public PrecheckBusinessUnitDto? BusinessUnit { get; set; }

    /// <summary>候选键（去重识别）</summary>
    [Required]
    public string CandidateKey { get; set; } = null!;

    /// <summary>候选集根路径</summary>
    [Required]
    public string SourceRoot { get; set; } = null!;

    /// <summary>备份业务时间</summary>
    public DateTime? BackupBusinessTime { get; set; }

    /// <summary>预检状态（passed / no_new_backup / still_changing / required_file_missing / size_abnormal / path_not_found / access_denied / failed）</summary>
    [Required]
    public string PrecheckStatus { get; set; } = null!;

    public int? TotalFiles { get; set; }
    public long? TotalBytes { get; set; }

    /// <summary>快速指纹（增量判断）</summary>
    public string? QuickFingerprint { get; set; }

    /// <summary>清单 SHA-256</summary>
    public string? ManifestHash { get; set; }

    /// <summary>失败码（预检未通过时）</summary>
    public string? FailureCode { get; set; }

    /// <summary>失败信息（预检未通过时）</summary>
    public string? FailureMessage { get; set; }

    /// <summary>文件清单（预检通过时）</summary>
    public List<PrecheckFileDto>? Files { get; set; }
}

/// <summary>预检业务单元</summary>
public class PrecheckBusinessUnitDto
{
    [Required]
    public string ExternalKey { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? SourceRelativePath { get; set; }
}

/// <summary>预检文件明细</summary>
public class PrecheckFileDto
{
    [Required]
    public string RelativePath { get; set; } = null!;

    public long SizeBytes { get; set; }
    public DateTime LastModifiedAt { get; set; }

    /// <summary>全量 SHA-256（可延迟计算，为空则服务端要求补算）</summary>
    public string? Sha256 { get; set; }

    /// <summary>快速哈希（头部采样）</summary>
    public string? QuickHash { get; set; }

    public bool IsRequired { get; set; }
}

/// <summary>提交预检结果响应（设计书 13.1）</summary>
public class SubmitPrecheckResultResponse
{
    public Guid? CandidateBackupSetId { get; set; }

    /// <summary>服务端是否接受该预检结果</summary>
    public bool Accepted { get; set; }

    /// <summary>wait_for_approval / wait_for_upload / wait_manual / rejected</summary>
    public string NextAction { get; set; } = null!;
}

/// <summary>任务识别测试结果响应（设计书 13.2，异步）</summary>
public class TestRecognitionResponse
{
    /// <summary>异步操作 ID（即预检指令 ID）</summary>
    public Guid OperationId { get; set; }

    /// <summary>accepted（已下发）</summary>
    public string Status { get; set; } = "accepted";
}
