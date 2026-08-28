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

    /// <summary>当前免令牌登记窗口的到期时间；已关闭或从未开过为空</summary>
    public DateTime? EnrollmentOpenUntilUtc { get; set; }

    /// <summary>当前窗口是否仍然有效（到期时间在将来）</summary>
    public bool EnrollmentWindowOpen { get; set; }

    /// <summary>
    /// 点一次「再开放」延长多少分钟。由服务端给出，界面不再自己写死一个数——
    /// 按钮上写「开放 30 分钟」而实际窗口是安装默认的 24 小时，
    /// 两个数字对不上，管理员自然会怀疑这个功能没生效。
    /// </summary>
    public int EnrollmentExtendMinutes { get; set; }

    /// <summary>
    /// 当前窗口是不是安装时的默认值（服务端首次启动 + 24 小时）而非有人手动开的。
    /// 界面据此说明这个到期时间是哪来的。
    /// </summary>
    public bool EnrollmentWindowIsInstallDefault { get; set; }

    public DateTime CheckedAtUtc { get; set; }
}
