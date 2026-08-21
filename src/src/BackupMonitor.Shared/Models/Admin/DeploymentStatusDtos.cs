namespace BackupMonitor.Shared.Models.Admin;

/// <summary>
/// 管理网页使用的部署状态摘要。此 DTO 只包含运行状态和版本信息，不能承载任何密钥、连接串或凭据。
/// </summary>
public sealed class DeploymentStatusDto
{
    public string ServerAddress { get; set; } = string.Empty;

    public string ServerStatus { get; set; } = "unknown";

    public string DatabaseStatus { get; set; } = "unknown";

    public string ClientInstallerVersion { get; set; } = "unknown";

    public string DeploymentMode { get; set; } = "Secure";

    public bool AutomaticEnrollment { get; set; }

    public DateTime? EnrollmentOpenUntilUtc { get; set; }

    public DateTime CheckedAtUtc { get; set; }
}
