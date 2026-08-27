using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>恢复范围选择（设计书 19.1；V1 仅支持 full 整套）</summary>
public class RestoreSelectionDto
{
    /// <summary>full=整个备份集</summary>
    public string Type { get; set; } = "full";
}

/// <summary>创建恢复请求（设计书 19.1）</summary>
public class CreateRestoreRequestDto
{
    [Required(ErrorMessage = "backupSetId 必填")]
    public Guid? BackupSetId { get; set; }

    [Required(ErrorMessage = "purpose 必填")]
    [MaxLength(1000, ErrorMessage = "purpose 不能超过 1000 字符")]
    public string Purpose { get; set; } = null!;

    /// <summary>恢复范围，缺省 full</summary>
    public RestoreSelectionDto? Selection { get; set; }
}

/// <summary>创建恢复请求响应（设计书 19.1）</summary>
public class CreateRestoreResponseDto
{
    public Guid RestoreRequestId { get; set; }

    /// <summary>snake_case 状态（创建后为 verifying）</summary>
    public string Status { get; set; } = null!;
}

/// <summary>恢复请求详情</summary>
public class RestoreRequestDto
{
    public Guid Id { get; set; }

    public Guid BackupSetId { get; set; }

    public string? BackupSetCode { get; set; }

    public string? ClientHostname { get; set; }

    public string Purpose { get; set; } = null!;

    /// <summary>snake_case 状态：requested/verifying/ready/downloading/completed/failed/expired</summary>
    public string Status { get; set; } = null!;

    public DateTime RequestedAt { get; set; }

    public DateTime? VerifiedAt { get; set; }

    public DateTime? DownloadExpiresAt { get; set; }

    public long DownloadedBytes { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ClientIp { get; set; }

    public string? ErrorMessage { get; set; }

    public Guid RequestedBy { get; set; }

    public string? RequestedByName { get; set; }

    /// <summary>
    /// 校验队列里排在本请求之前还有多少个工作项（审计 H-18）。
    ///
    /// 只在 verifying 状态下有值。原先「点了恢复之后一直在校验」的时候，
    /// 界面上完全看不出到底是卡住了还是在排队——这两件事对使用者是
    /// 完全不同的两种处境，一个该等，一个该报障。
    /// </summary>
    public int? VerificationQueueLength { get; set; }
}

/// <summary>签发下载令牌响应（补充设计：库中只存令牌哈希，明文仅在签发时返回一次）</summary>
public class RestoreDownloadTokenDto
{
    public Guid RestoreRequestId { get; set; }

    public string DownloadToken { get; set; } = null!;

    /// <summary>下载地址（相对路径，前端拼接服务基址）</summary>
    public string DownloadUrl { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }
}

/// <summary>恢复请求列表查询</summary>
public class RestoreQuery : PagedQuery
{
    public Guid? BackupSetId { get; set; }

    /// <summary>snake_case 状态筛选</summary>
    public string? Status { get; set; }
}
