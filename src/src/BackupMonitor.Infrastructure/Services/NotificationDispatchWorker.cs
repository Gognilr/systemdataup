using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 通知发送后台工作器（设计书 25 定时任务"告警通知"；第二批补充设计的落地实现）：
/// 轮询 notification_deliveries 中 pending 记录，按渠道真实投递（SMTP/企业微信 webhook/钉钉 webhook），
/// 回写 sent/failed 与尝试次数；超过最大尝试次数置 failed。
/// 单实例（进程内单 BackgroundService），幂等（只处理 pending 且满足重试间隔的记录）。
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
    private readonly ILogger<NotificationDispatchWorker> _logger;

    public NotificationDispatchWorker(IServiceScopeFactory scopeFactory, ILogger<NotificationDispatchWorker> logger)
    {
        _scopeFactory = scopeFactory;
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

        var channelSettings = await LoadChannelSettingsAsync(db, ct);

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

        foreach (var delivery in pending)
        {
            ct.ThrowIfCancellationRequested();
            await DispatchOneAsync(db, delivery, channelSettings, maxAttempts, ct);
        }
    }

    /// <summary>单条投递与状态回写（异常隔离，单条失败不影响其余）</summary>
    private async Task DispatchOneAsync(
        AppDbContext db,
        Core.Entities.Alert.NotificationDelivery delivery,
        NotificationSettingsDto settings,
        int maxAttempts,
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
                        await SendEmailAsync(settings.Email, delivery, ct);
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

    /// <summary>SMTP 邮件投递</summary>
    private static async Task SendEmailAsync(
        EmailChannelSettingsDto email,
        Core.Entities.Alert.NotificationDelivery delivery,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email.SmtpHost))
            throw new InvalidOperationException("SMTP 服务器未配置");

        var subject = $"[备份监控] {delivery.Alert?.Title ?? "系统告警"}";
        var body = new StringBuilder()
            .AppendLine(delivery.Alert?.Title)
            .AppendLine()
            .AppendLine(delivery.Alert?.Message)
            .AppendLine()
            .AppendLine($"告警等级: {delivery.Alert?.Level}")
            .AppendLine($"发生时间: {delivery.Alert?.LastOccurredAt:yyyy-MM-dd HH:mm:ss} UTC")
            .ToString();

        using var client = new SmtpClient(email.SmtpHost, email.SmtpPort);
        if (!string.IsNullOrWhiteSpace(email.SmtpUsername))
            client.Credentials = new NetworkCredential(email.SmtpUsername, email.SmtpPassword);

        using var message = new MailMessage
        {
            From = new MailAddress(string.IsNullOrWhiteSpace(email.FromAddress) ? "backup-monitor@localhost" : email.FromAddress),
            Subject = subject,
            Body = body,
            BodyEncoding = Encoding.UTF8
        };
        message.To.Add(delivery.Recipient);

        await client.SendMailAsync(message, ct);
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

    /// <summary>读取渠道配置（与 NotificationService 同一键同一形状）</summary>
    private async Task<NotificationSettingsDto> LoadChannelSettingsAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SettingKey == NotificationService.SettingsKey, ct);
        if (row is null)
            return new NotificationSettingsDto();

        try
        {
            return JsonSerializer.Deserialize<NotificationSettingsDto>(row.SettingValue, JsonOpts)
                   ?? new NotificationSettingsDto();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "通知渠道配置反序列化失败，本轮按默认（全部禁用）处理");
            return new NotificationSettingsDto();
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
