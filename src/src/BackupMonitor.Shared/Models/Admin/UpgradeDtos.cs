using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>Agent 升级请求（第二批补充设计：经 upgrade_agent 指令通道下发）</summary>
public class UpgradeAgentRequestDto
{
    [Required(ErrorMessage = "clientIds 必填")]
    [MinLength(1, ErrorMessage = "至少需要一台客户端")]
    public List<Guid> ClientIds { get; set; } = [];

    [Required(ErrorMessage = "targetVersion 必填")]
    [MaxLength(32, ErrorMessage = "targetVersion 不能超过 32 字符")]
    public string TargetVersion { get; set; } = null!;

    /// <summary>升级包 HTTP(S) 地址；生产环境必须使用 HTTPS。</summary>
    [Required(ErrorMessage = "packageUrl 必填")]
    [Url(ErrorMessage = "packageUrl 必须是有效的 HTTP(S) 地址")]
    [MaxLength(2048, ErrorMessage = "packageUrl 不能超过 2048 字符")]
    public string PackageUrl { get; set; } = null!;

    /// <summary>升级包 SHA-256（64 位十六进制），Agent 强制校验</summary>
    [Required(ErrorMessage = "packageSha256 必填")]
    [RegularExpression("^[0-9a-fA-F]{64}$", ErrorMessage = "packageSha256 必须为 64 位十六进制")]
    public string PackageSha256 { get; set; } = null!;

    [MaxLength(500, ErrorMessage = "note 不能超过 500 字符")]
    public string? Note { get; set; }
}

/// <summary>单台客户端下发结果（批量接口返回逐项结果，设计书 2.2）</summary>
public class UpgradeClientResultDto
{
    public Guid ClientId { get; set; }

    public string? Hostname { get; set; }

    public bool Success { get; set; }

    public Guid? CommandId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>Agent 升级下发响应</summary>
public class UpgradeAgentResponseDto
{
    public int Dispatched { get; set; }

    public int Failed { get; set; }

    public List<UpgradeClientResultDto> Items { get; set; } = [];
}
