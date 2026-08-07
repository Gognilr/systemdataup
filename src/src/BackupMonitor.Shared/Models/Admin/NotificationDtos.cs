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

    public int SmtpPort { get; set; } = 25;

    public string? SmtpUsername { get; set; }

    /// <summary>SMTP 密码（可选，空则匿名发送）</summary>
    public string? SmtpPassword { get; set; }

    public string? FromAddress { get; set; }
}

/// <summary>Webhook 渠道配置（企业微信/钉钉）</summary>
public class WebhookChannelSettingsDto
{
    public bool Enabled { get; set; }

    public string? WebhookUrl { get; set; }
}

/// <summary>通知渠道配置（存 system_settings 键 notification_channels）</summary>
public class NotificationSettingsDto
{
    public EmailChannelSettingsDto Email { get; set; } = new();

    public WebhookChannelSettingsDto Wecom { get; set; } = new();

    public WebhookChannelSettingsDto Dingtalk { get; set; } = new();
}
