using System.Text;
using System.Text.Json;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 通知发送后台工作器（设计书 25 定时任务"告警通知"；第二批补充设计的落地实现）：
/// 轮询 notification_deliveries 中 pending 记录，按渠道真实投递（SMTP/企业微信 webhook/钉钉 webhook），
/// 回写 sent/failed 与尝试次数；超过最大尝试次数置 failed。
/// 单实例（进程内单 BackgroundService），幂等（只处理 pending 且满足重试间隔的记录）。
///
/// 整改批次 C · C2：SMTP 发信换用 MailKit（微软官方文档标注 System.Net.Mail.SmtpClient 不建议
/// 用于新开发），支持 STARTTLS / 隐式 TLS，能连上真实邮箱服务商（腾讯企业邮、Exchange Online 等
/// 全部强制 TLS，旧实现从不设置 EnableSsl，连接直接失败）。
/// </summary>
public class NotificationDispatchWorker : BackgroundService
{
    /// <summary>轮询间隔（秒）system_settings 键</summary>
    public const string IntervalKey = "notification_dispatch_interval_seconds";

    /// <summary>最大尝试次数 system_settings 键</summary>
    public const string MaxAttemptsKey = "notification_max_attempts";

    /// <summary>重试间隔（分钟）system_settings 键</summary>
    public const string RetryIntervalKey = "notification_retry_interval_minutes";

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:notification_dispatch";

    /// <summary>锁 TTL：须大于单轮最坏耗时（50 条 × 15s HTTP 超时 ≈ 12.5 分钟），防止执行中被抢占重复发送</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(15);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationDispatchWorker> _logger;

    public NotificationDispatchWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<NotificationDispatchWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("通知发送工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 30;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                intervalSeconds = Math.Max(5, await settings.GetIntAsync(IntervalKey, 30, stoppingToken));

                // 单实例锁（设计书 §25）：多实例部署时同一时刻仅一个实例执行派发
                var scheduledLock = scope.ServiceProvider.GetRequiredService<IScheduledLockService>();
                if (await scheduledLock.TryAcquireAsync(LockKey, LockTtl, stoppingToken))
                {
                    try
                    {
                        await DispatchBatchAsync(scope, stoppingToken);
                    }
                    finally
                    {
                        await scheduledLock.ReleaseAsync(LockKey, CancellationToken.None);
                    }
                }
                // 未抢到锁说明其他实例正在执行，本轮跳过，走循环末尾的统一 Delay
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通知发送轮询异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>一轮投递：取出待发送记录并逐条处理</summary>
    private async Task DispatchBatchAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settingsProvider = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();

        var maxAttempts = Math.Max(1, await settingsProvider.GetIntAsync(MaxAttemptsKey, 3, ct));
        var retryMinutes = Math.Max(0, await settingsProvider.GetIntAsync(RetryIntervalKey, 5, ct));
        var retryCutoff = DateTime.UtcNow.AddMinutes(-retryMinutes);

        // 整改批次 C · C1：渠道配置（含 SMTP 密码 / webhook 地址）现在加密存储，
        // 统一走 INotificationService.GetSettingsAsync 解密，同时复用它的历史明文惰性迁移逻辑，
        // 不在本文件里重复维护一份解密/迁移代码。
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var channelSettings = await notificationService.GetSettingsAsync(ct);

        var pending = await db.NotificationDeliveries
            .Include(d => d.Alert)
            .Where(d => d.Status == NotificationStatus.Pending
                        && d.AttemptCount < maxAttempts
                        && (d.LastAttemptAt == null || d.LastAttemptAt < retryCutoff))
            .OrderBy(d => d.Id)
            .Take(50)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        _logger.LogInformation("通知发送：本轮待投递 {Count} 条", pending.Count);

        var reportTimezone = ResolveTimeZone(_configuration["Reports:Timezone"]);

        foreach (var delivery in pending)
        {
            ct.ThrowIfCancellationRequested();
            await DispatchOneAsync(db, delivery, channelSettings, maxAttempts, reportTimezone, ct);
        }
    }

    /// <summary>单条投递与状态回写（异常隔离，单条失败不影响其余）</summary>
    private async Task DispatchOneAsync(
        AppDbContext db,
        Core.Entities.Alert.NotificationDelivery delivery,
        NotificationSettingsDto settings,
        int maxAttempts,
        TimeZoneInfo reportTimezone,
        CancellationToken ct)
    {
        string? error = null;
        try
        {
            switch (delivery.Channel)
            {
                case NotificationChannel.Email:
                    if (!settings.Email.Enabled)
                        error = "邮件渠道未启用";
                    else
                        await SendEmailAsync(settings.Email, delivery, reportTimezone, ct);
                    break;

                case NotificationChannel.Wecom:
                    if (!settings.Wecom.Enabled)
                        error = "企业微信渠道未启用";
                    else
                        await PostWebhookAsync(delivery.Recipient, delivery, ct);
                    break;

                case NotificationChannel.Dingtalk:
                    if (!settings.Dingtalk.Enabled)
                        error = "钉钉渠道未启用";
                    else
                        await PostWebhookAsync(delivery.Recipient, delivery, ct);
                    break;

                default:
                    error = $"未知渠道 {delivery.Channel}";
                    break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "通知投递失败 delivery={DeliveryId} channel={Channel}", delivery.Id, delivery.Channel);
        }

        delivery.AttemptCount++;
        delivery.LastAttemptAt = DateTime.UtcNow;

        if (error is null)
        {
            delivery.Status = NotificationStatus.Sent;
            delivery.SentAt = DateTime.UtcNow;
            delivery.ErrorMessage = null;
        }
        else
        {
            delivery.ErrorMessage = error.Length > 2000 ? error[..2000] : error;
            // 超过最大尝试次数才置 failed，否则保持 pending 等待下轮重试
            if (delivery.AttemptCount >= maxAttempts)
                delivery.Status = NotificationStatus.Failed;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>SMTP 邮件投递（告警通知场景，正文按报表时区渲染）</summary>
    private static Task SendEmailAsync(
        EmailChannelSettingsDto email,
        Core.Entities.Alert.NotificationDelivery delivery,
        TimeZoneInfo reportTimezone,
        CancellationToken ct)
    {
        var occurredAtUtc = delivery.Alert?.LastOccurredAt;
        var occurredLocal = occurredAtUtc is null
            ? (DateTime?)null
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(occurredAtUtc.Value, DateTimeKind.Utc), reportTimezone);

        var subject = $"[备份监控] {delivery.Alert?.Title ?? "系统告警"}";
        var body = new StringBuilder()
            .AppendLine(delivery.Alert?.Title)
            .AppendLine()
            .AppendLine(delivery.Alert?.Message)
            .AppendLine()
            .AppendLine($"告警等级: {delivery.Alert?.Level}")
            // C2 第4点：时间改按系统配置的报表时区渲染，并标注时区名（不再写死 UTC）
            .AppendLine($"发生时间: {occurredLocal:yyyy-MM-dd HH:mm:ss} ({reportTimezone.Id})")
            .ToString();

        return SendEmailCoreAsync(email, delivery.Recipient, subject, body, ct);
    }

    /// <summary>
    /// 整改批次 C · C2：发送测试邮件（管理端「发送测试邮件」按钮，见 AdminNotificationController）。
    /// 不落任何 notification_deliveries 记录，SMTP 层异常原样向上抛，由调用方把 MailKit/服务器
    /// 返回的原始错误信息（如 535 Authentication failed）带回界面。
    /// </summary>
    public static Task SendTestEmailAsync(EmailChannelSettingsDto email, string recipient, CancellationToken ct)
    {
        var body = $"这是一封来自「轻量级集中备份采集与监控系统」的测试邮件，用于验证 SMTP 配置是否正确。" +
                   $"{Environment.NewLine}发送时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        return SendEmailCoreAsync(email, recipient, "[备份监控] 测试邮件", body, ct);
    }

    /// <summary>MailKit 发信核心逻辑，供正式告警投递与"发送测试邮件"共用</summary>
    private static async Task SendEmailCoreAsync(
        EmailChannelSettingsDto email,
        string recipient,
        string subject,
        string body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email.SmtpHost))
            throw new InvalidOperationException("SMTP 服务器未配置");
        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException("收件人为空");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(
            string.IsNullOrWhiteSpace(email.FromAddress) ? "backup-monitor@localhost" : email.FromAddress));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        var secureSocketOptions = (email.SecurityMode ?? "auto").Trim().ToLowerInvariant() switch
        {
            "none" => SecureSocketOptions.None,
            "starttls" => SecureSocketOptions.StartTls,
            "ssl" => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.Auto
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(email.SmtpHost, email.SmtpPort, secureSocketOptions, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(email.SmtpUsername))
                await client.AuthenticateAsync(email.SmtpUsername, email.SmtpPassword ?? string.Empty, ct);

            await client.SendAsync(message, ct);
        }
        finally
        {
            await client.DisconnectAsync(true, ct);
        }
    }

    /// <summary>企业微信/钉钉机器人 webhook 投递（文本消息）</summary>
    private static async Task PostWebhookAsync(
        string webhookUrl,
        Core.Entities.Alert.NotificationDelivery delivery,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl) || !Uri.TryCreate(webhookUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("webhook 地址无效");

        var content = new StringBuilder()
            .AppendLine($"[备份监控告警] {delivery.Alert?.Title ?? "系统告警"}")
            .AppendLine($"等级: {delivery.Alert?.Level}")
            .AppendLine($"时间: {delivery.Alert?.LastOccurredAt:yyyy-MM-dd HH:mm:ss} UTC")
            .AppendLine(delivery.Alert?.Message)
            .ToString();

        var payload = JsonSerializer.Serialize(new { msgtype = "text", text = new { content } }, JsonOpts);
        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await Http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"webhook 返回 HTTP {(int)response.StatusCode}: {Truncate(responseBody, 200)}");

        // 企业微信/钉钉成功时返回 errcode=0；errcode!=0 视为失败
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("errcode", out var errcode) && errcode.TryGetInt32(out var code) && code != 0)
            {
                var errmsg = doc.RootElement.TryGetProperty("errmsg", out var m) ? m.GetString() : null;
                throw new InvalidOperationException($"webhook errcode={code} errmsg={errmsg}");
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应且 HTTP 2xx：视为成功
        }
    }

    /// <summary>
    /// 解析报表时区（与 ReportService.ResolveTimeZone 相同的回退链）：
    /// IANA ID 优先，找不到时尝试常见 Windows 时区 ID 映射，都失败则退回 UTC。
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            var windowsId = id.Trim() switch
            {
                "Asia/Tokyo" => "Tokyo Standard Time",
                "Asia/Shanghai" => "China Standard Time",
                "Asia/Singapore" => "Singapore Standard Time",
                "Europe/London" => "GMT Standard Time",
                "America/New_York" => "Eastern Standard Time",
                "America/Los_Angeles" => "Pacific Standard Time",
                _ => null
            };

            if (windowsId is not null)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
                catch (TimeZoneNotFoundException) { }
            }

            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
