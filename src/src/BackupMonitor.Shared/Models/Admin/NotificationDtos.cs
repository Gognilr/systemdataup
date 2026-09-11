namespace BackupMonitor.Shared.Models.Admin;

/// <summary>通知发送记录（数据表 notification_deliveries）</summary>
public class NotificationDeliveryDto
{
    public Guid Id { get; set; }

    public Guid? AlertId { get; set; }

    public string? AlertTitle { get; set; }

    /// <summary>snake_case 渠道：email/wecom/dingtalk</summary>
    public string Channel { get; set; } = null!;

    public string Recipient { get; set; } = null!;

    /// <summary>snake_case 状态：pending/sent/failed</summary>
    public string Status { get; set; } = null!;

    public int AttemptCount { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    public DateTime? SentAt { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>通知发送记录查询</summary>
public class NotificationDeliveryQuery : PagedQuery
{
    public Guid? AlertId { get; set; }

    /// <summary>snake_case 渠道筛选</summary>
    public string? Channel { get; set; }

    /// <summary>snake_case 状态筛选</summary>
    public string? Status { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

/// <summary>邮件渠道配置</summary>
public class EmailChannelSettingsDto
{
    public bool Enabled { get; set; }

    /// <summary>收件人列表（最多 20 个）</summary>
    public List<string> Recipients { get; set; } = [];

    public string? SmtpHost { get; set; }

    /// <summary>整改批次 C · C2：默认端口从 25 改为 587（STARTTLS 标准端口），25 基本发不出去</summary>
    public int SmtpPort { get; set; } = 587;

    public string? SmtpUsername { get; set; }

    /// <summary>
    /// SMTP 密码（可选，空则匿名发送）。
    /// 整改批次 C · C1：库中加密存储；对外只读接口（GetSettingsForDisplayAsync）返回时
    /// 有值一律替换为 <see cref="NotificationSecretMask.Unchanged"/>，无值返回 null，绝不回显明文。
    /// 保存时若收到该掩码常量，保留库里原值不变。
    /// </summary>
    public string? SmtpPassword { get; set; }

    public string? FromAddress { get; set; }

    /// <summary>
    /// 整改批次 C · C2：SMTP 安全模式，auto/none/starttls/ssl，默认 auto（按端口自动协商）。
    /// </summary>
    public string SecurityMode { get; set; } = "auto";
}

/// <summary>Webhook 渠道配置（企业微信/钉钉）</summary>
public class WebhookChannelSettingsDto
{
    public bool Enabled { get; set; }

    /// <summary>
    /// webhook 地址（含 access_token，本身即凭据）。
    /// 整改批次 C · C1：库中加密存储，对外只读接口按 <see cref="NotificationSecretMask"/> 约定掩码。
    /// </summary>
    public string? WebhookUrl { get; set; }
}

/// <summary>通知渠道配置（存 system_settings 键 notification_channels）</summary>
public class NotificationSettingsDto
{
    public EmailChannelSettingsDto Email { get; set; } = new();

    public WebhookChannelSettingsDto Wecom { get; set; } = new();

    public WebhookChannelSettingsDto Dingtalk { get; set; } = new();
}

/// <summary>整改批次 C · C1：凭据掩码约定</summary>
public static class NotificationSecretMask
{
    /// <summary>
    /// 只读接口回显凭据字段时的占位常量：有值返回该常量，无值返回 null。
    /// 保存接口收到该常量时保留库里原值不变，不会把占位符本身当密码存进去。
    /// </summary>
    public const string Unchanged = "__UNCHANGED__";
}

/// <summary>整改批次 C · C2：发送测试邮件请求</summary>
public class NotificationTestEmailRequest
{
    /// <summary>待测试的邮件渠道配置；SmtpPassword 允许传 <see cref="NotificationSecretMask.Unchanged"/>，
    /// 服务端会替换为库中当前保存的密码后再发送</summary>
    public EmailChannelSettingsDto Email { get; set; } = new();

    /// <summary>测试收件人；不填则取 Email.Recipients 的第一个</summary>
    public string? Recipient { get; set; }
}

/// <summary>
/// 通知渠道是否已配置（R5）。刻意只有一个布尔字段：
/// 概览页要回答的问题就一个——告警发不发得出去。渠道细节属于配置页。
/// </summary>
public class NotificationChannelStatusDto
{
    /// <summary>至少有一个渠道已启用且填全。false 表示告警只会出现在管理网页上。</summary>
    public bool Configured { get; set; }
}
