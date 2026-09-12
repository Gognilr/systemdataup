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

/// <summary>
/// 一个渠道的投递筛选（整改清单 2026-09-11 · R24）。
///
/// 在此之前只有渠道总开关：启用邮件 = 每一条告警都发。20 多类告警里
/// 「客户端 CPU 吃紧」「客户端版本落后」这种最容易变成没人看的背景噪音，
/// 而它们一旦被习惯性忽略，「备份复查未通过」也会跟着一起被忽略——
/// 报警的可信度是整体的，摊薄了就都没了。
/// </summary>
public class NotificationFilterDto
{
    /// <summary>
    /// 最低等级：notice（全发）/ warning（默认，Critical + Warning）/ critical（只发严重）。
    ///
    /// 为空按 warning 处理。老配置里没有这个字段，因此升级之后默认就是 Critical + Warning：
    /// 那两条 Notice 级（客户端登记成功、预检结果可疑）不再进邮箱，管理网页上照样看得到。
    /// </summary>
    public string? MinLevel { get; set; }

    /// <summary>不走这个渠道的告警类别（键见 <see cref="AlertCategoryCatalog"/>）。</summary>
    public List<string> ExcludedCategories { get; set; } = [];
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

    /// <summary>这个渠道发哪些告警（R24）。</summary>
    public NotificationFilterDto Filter { get; set; } = new();
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

    /// <summary>这个渠道发哪些告警（R24）。三个渠道各有各的筛选：
    /// 群里可以收全量，邮箱只收要紧的。</summary>
    public NotificationFilterDto Filter { get; set; } = new();
}

/// <summary>通知渠道配置（存 system_settings 键 notification_channels）</summary>
public class NotificationSettingsDto
{
    public EmailChannelSettingsDto Email { get; set; } = new();

    public WebhookChannelSettingsDto Wecom { get; set; } = new();

    public WebhookChannelSettingsDto Dingtalk { get; set; } = new();

    /// <summary>
    /// 可选的告警类别清单，只在**读取**时填充，不落库（BuildEncryptedCopy 不带它）。
    /// 让界面上的勾选框由服务端这一份唯一清单驱动，免得前端再抄一份然后漏项。
    /// </summary>
    public List<AlertCategoryOptionDto> AvailableCategories { get; set; } = [];
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
/// 「发送测试消息」请求（企业微信 / 钉钉）。
/// WebhookUrl 允许传 <see cref="NotificationSecretMask.Unchanged"/>，
/// 服务端会替换为库中当前保存的地址后再发送——这样用户不必为了测试把
/// 含 access_token 的地址重新粘一遍。
/// </summary>
public class NotificationTestWebhookRequest
{
    /// <summary>渠道：wecom / dingtalk</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>待测试的 webhook 地址；传掩码常量则用库中已保存的地址</summary>
    public string? WebhookUrl { get; set; }
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

/// <summary>
/// 告警触发阈值（整改清单 2026-09-11 · R25）。
///
/// 这四个值此前只存在于 system_settings 表里，界面上没有任何入口——
/// 「客户端 CPU 使用率过高」按 85% 报，而一台跑着 SQL Server 的机器常年就在 90% 上下，
/// 于是它每天都在报，报到没人看。要调只能手工改库，而知道该改哪个键的人只有写代码的。
///
/// 注意这里管的是**告警什么时候产生**，与通知筛选（管的是产生之后发不发）是两件事。
/// </summary>
/// <summary>每日健康快报的开关与发送时刻。</summary>
public class DailyDigestSettingsDto
{
    /// <summary>开着才发。默认关——备份节奏稀疏时它会退化成每天一封「什么都没发生」。</summary>
    public bool Enabled { get; set; }

    /// <summary>按报表时区的发送小时（0~23）。</summary>
    public int Hour { get; set; } = 9;
}

public class AlertThresholdSettingsDto
{
    /// <summary>
    /// 告警恢复时补发一条「已恢复」。默认开。
    ///
    /// 「没有新消息」在收件人那里读起来和「已经好了」是一样的，而这两者差别很大。
    /// 代价是消息量接近翻倍：坏一次、好一次。嫌吵可以关掉。
    /// </summary>
    public bool RecoveryNotify { get; set; } = true;

    /// <summary>CPU 使用率达到多少报警（%），1~100。默认 85。</summary>
    public int ClientCpuPercent { get; set; } = 85;

    /// <summary>内存使用率达到多少报警（%），1~100。默认 90。</summary>
    public int ClientMemoryPercent { get; set; } = 90;

    /// <summary>源盘可用空间低于多少报警（%），1~99。默认 10。</summary>
    public int ClientDiskFreePercent { get; set; } = 10;

    /// <summary>同一条告警一直不恢复时，隔多少小时补发一次通知，1~720。默认见服务端常量。</summary>
    public int RenotifyHours { get; set; } = 24;
}
