using System.Text.Json;
using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>通知服务（第二批补充设计：发送记录查询 + 渠道配置读写；实际发送通道后续实现）</summary>
public interface INotificationService
{
    Task<PagedResult<NotificationDeliveryDto>> GetDeliveriesAsync(NotificationDeliveryQuery query, CancellationToken ct = default);

    /// <summary>
    /// 内部用（后台发送 worker）：返回解密后的明文凭据。
    /// 整改批次 C · C1：绝不能把这个方法的返回值直接暴露给任何 HTTP 响应。
    /// 首次读到历史明文凭据时会就地重新加密（惰性迁移），日志记一条 INFO。
    /// </summary>
    Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>控制器用：凭据字段替换为掩码（<see cref="NotificationSecretMask"/>），绝不回显明文</summary>
    Task<NotificationSettingsDto> GetSettingsForDisplayAsync(CancellationToken ct = default);

    /// <summary>
    /// 通知渠道是否至少有一个在用（R5）。
    ///
    /// 单开一个方法而不是让前端读整份配置：概览页人人看得见，
    /// 而整份配置要 perm:system.manage 且带着 SMTP 主机、收件人、webhook 地址——
    /// 为了回答一个「是/否」把那些一并发出去没有道理。
    /// </summary>
    Task<bool> HasEnabledChannelAsync(CancellationToken ct = default);

    Task<NotificationSettingsDto> UpdateSettingsAsync(NotificationSettingsDto request, CancellationToken ct = default);
}

/// <summary>通知实现</summary>
public class NotificationService : INotificationService
{
    /// <summary>渠道配置在 system_settings 中的键</summary>
    public const string SettingsKey = "notification_channels";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppDbContext _db;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ISecretProtector _protector;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        AppDbContext db,
        ICurrentContext context,
        IAuditRecorder audit,
        ISecretProtector protector,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _context = context;
        _audit = audit;
        _protector = protector;
        _logger = logger;
    }

    public async Task<PagedResult<NotificationDeliveryDto>> GetDeliveriesAsync(NotificationDeliveryQuery query, CancellationToken ct = default)
    {
        var q = _db.NotificationDeliveries.AsNoTracking().AsQueryable();

        if (query.AlertId is not null)
            q = q.Where(d => d.AlertId == query.AlertId);

        if (!string.IsNullOrWhiteSpace(query.Channel))
        {
            if (!EnumMapping.TryParseSnakeCase<NotificationChannel>(query.Channel, out var channel))
                throw new BusinessException("INVALID_REQUEST", $"无效的通知渠道：{query.Channel}", 400);
            q = q.Where(d => d.Channel == channel);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!EnumMapping.TryParseSnakeCase<NotificationStatus>(query.Status, out var status))
                throw new BusinessException("INVALID_REQUEST", $"无效的通知状态：{query.Status}", 400);
            q = q.Where(d => d.Status == status);
        }

        if (query.From is not null)
            q = q.Where(d => (d.SentAt ?? d.LastAttemptAt) >= query.From);
        if (query.To is not null)
            q = q.Where(d => (d.SentAt ?? d.LastAttemptAt) <= query.To);

        var totalCount = await q.LongCountAsync(ct);

        var rows = await q
            .OrderByDescending(d => d.SentAt ?? d.LastAttemptAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(d => new
            {
                d.Id,
                d.AlertId,
                AlertTitle = d.Alert != null ? d.Alert.Title : null,
                d.Channel,
                d.Recipient,
                d.Status,
                d.AttemptCount,
                d.LastAttemptAt,
                d.SentAt,
                d.ErrorMessage
            })
            .ToListAsync(ct);

        var items = rows.Select(d => new NotificationDeliveryDto
        {
            Id = d.Id,
            AlertId = d.AlertId,
            AlertTitle = d.AlertTitle,
            Channel = EnumMapping.ToSnakeCase(d.Channel),
            Recipient = d.Recipient,
            Status = EnumMapping.ToSnakeCase(d.Status),
            AttemptCount = d.AttemptCount,
            LastAttemptAt = d.LastAttemptAt,
            SentAt = d.SentAt,
            ErrorMessage = d.ErrorMessage
        }).ToList();

        return PagedResult<NotificationDeliveryDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        // 注意：不能用 AsNoTracking——命中惰性迁移分支时要把重新加密后的值写回同一行。
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == SettingsKey, ct);
        if (row is null)
            return new NotificationSettingsDto();

        NotificationSettingsDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<NotificationSettingsDto>(row.SettingValue, JsonOpts)
                  ?? new NotificationSettingsDto();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "通知渠道配置 {Key} 反序列化失败，返回默认配置", SettingsKey);
            return new NotificationSettingsDto();
        }

        // C1 第5点：历史明文惰性迁移判定——库里存的是"未带 enc:v1: 前缀"的明文
        var needsMigration =
            (!string.IsNullOrEmpty(dto.Email.SmtpPassword) && !_protector.IsProtected(dto.Email.SmtpPassword)) ||
            (!string.IsNullOrEmpty(dto.Wecom.WebhookUrl) && !_protector.IsProtected(dto.Wecom.WebhookUrl)) ||
            (!string.IsNullOrEmpty(dto.Dingtalk.WebhookUrl) && !_protector.IsProtected(dto.Dingtalk.WebhookUrl));

        // 解密返回给调用方（未加密的历史明文原样返回，Unprotect 对非密文原样透传）
        dto.Email.SmtpPassword = _protector.Unprotect(dto.Email.SmtpPassword);
        dto.Wecom.WebhookUrl = _protector.Unprotect(dto.Wecom.WebhookUrl);
        dto.Dingtalk.WebhookUrl = _protector.Unprotect(dto.Dingtalk.WebhookUrl);

        if (needsMigration)
        {
            row.SettingValue = JsonSerializer.Serialize(BuildEncryptedCopy(dto), JsonOpts);
            row.Encrypted = true;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "通知渠道配置 {Key} 检测到历史明文凭据（SMTP 密码/企业微信或钉钉 webhook），已就地重新加密。" +
                "这些凭据此前可能已经明文写入过审计日志，视为已泄露，请尽快重置 SMTP 密码并重新生成机器人 webhook。",
                SettingsKey);
        }

        return dto;
    }

    /// <summary>控制器用：凭据字段替换为掩码，绝不回显明文（C1 第3点）</summary>
    public async Task<bool> HasEnabledChannelAsync(CancellationToken ct = default)
    {
        // 「配置行不存在」和「三个渠道全部 Enabled = false」是同一件事：
        // 告警只会出现在网页上。后者尤其容易被当成已配置——界面上明明填着 SMTP 服务器。
        var settings = await GetSettingsAsync(ct);
        return settings.Email.Enabled
               || (settings.Wecom.Enabled && !string.IsNullOrWhiteSpace(settings.Wecom.WebhookUrl))
               || (settings.Dingtalk.Enabled && !string.IsNullOrWhiteSpace(settings.Dingtalk.WebhookUrl));
    }

    public async Task<NotificationSettingsDto> GetSettingsForDisplayAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        return Mask(settings);
    }

    public async Task<NotificationSettingsDto> UpdateSettingsAsync(NotificationSettingsDto request, CancellationToken ct = default)
    {
        // C1 第3点：请求里带掩码常量的字段，用库中当前解密值回填，不覆盖成占位符本身
        var existing = await GetSettingsAsync(ct);
        if (request.Email.SmtpPassword == NotificationSecretMask.Unchanged)
            request.Email.SmtpPassword = existing.Email.SmtpPassword;
        if (request.Wecom.WebhookUrl == NotificationSecretMask.Unchanged)
            request.Wecom.WebhookUrl = existing.Wecom.WebhookUrl;
        if (request.Dingtalk.WebhookUrl == NotificationSecretMask.Unchanged)
            request.Dingtalk.WebhookUrl = existing.Dingtalk.WebhookUrl;

        Validate(request);

        // C1 第2点：写入前把凭据字段换成密文
        var json = JsonSerializer.Serialize(BuildEncryptedCopy(request), JsonOpts);
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == SettingsKey, ct);

        if (row is null)
        {
            row = new SystemSetting
            {
                SettingKey = SettingsKey,
                SettingValue = json,
                Encrypted = true,
                UpdatedBy = _context.UserId
            };
            _db.SystemSettings.Add(row);
        }
        else
        {
            row.SettingValue = json;
            row.Encrypted = true;
            row.UpdatedBy = _context.UserId;
        }

        await _db.SaveChangesAsync(ct);

        // C1 第4点：审计只记非机密字段，绝不整包序列化 request
        var auditPayload = JsonSerializer.Serialize(new
        {
            email = new
            {
                request.Email.Enabled,
                request.Email.SmtpHost,
                request.Email.SmtpPort,
                request.Email.SecurityMode,
                recipientCount = request.Email.Recipients.Count
            },
            wecom = new { request.Wecom.Enabled },
            dingtalk = new { request.Dingtalk.Enabled }
        }, JsonOpts);

        await _audit.RecordAsync("notification.update_settings", AuditResult.Success, "system_setting", null,
            afterData: auditPayload, ct: ct);

        _logger.LogInformation("通知渠道配置已更新 email={Email} wecom={Wecom} dingtalk={Dingtalk}",
            request.Email.Enabled, request.Wecom.Enabled, request.Dingtalk.Enabled);

        // 返回值同样掩码，避免保存接口的响应体里回显明文
        return Mask(request);
    }

    /// <summary>克隆一份并把凭据字段换成密文，用于落库</summary>
    private NotificationSettingsDto BuildEncryptedCopy(NotificationSettingsDto source) => new()
    {
        Email = new EmailChannelSettingsDto
        {
            Enabled = source.Email.Enabled,
            Recipients = source.Email.Recipients,
            SmtpHost = source.Email.SmtpHost,
            SmtpPort = source.Email.SmtpPort,
            SmtpUsername = source.Email.SmtpUsername,
            SmtpPassword = _protector.Protect(source.Email.SmtpPassword),
            FromAddress = source.Email.FromAddress,
            SecurityMode = source.Email.SecurityMode
        },
        Wecom = new WebhookChannelSettingsDto
        {
            Enabled = source.Wecom.Enabled,
            WebhookUrl = _protector.Protect(source.Wecom.WebhookUrl)
        },
        Dingtalk = new WebhookChannelSettingsDto
        {
            Enabled = source.Dingtalk.Enabled,
            WebhookUrl = _protector.Protect(source.Dingtalk.WebhookUrl)
        }
    };

    /// <summary>克隆一份并把凭据字段替换为掩码：有值 → __UNCHANGED__，无值 → null</summary>
    private static NotificationSettingsDto Mask(NotificationSettingsDto source) => new()
    {
        Email = new EmailChannelSettingsDto
        {
            Enabled = source.Email.Enabled,
            Recipients = source.Email.Recipients,
            SmtpHost = source.Email.SmtpHost,
            SmtpPort = source.Email.SmtpPort,
            SmtpUsername = source.Email.SmtpUsername,
            SmtpPassword = string.IsNullOrEmpty(source.Email.SmtpPassword) ? null : NotificationSecretMask.Unchanged,
            FromAddress = source.Email.FromAddress,
            SecurityMode = source.Email.SecurityMode
        },
        Wecom = new WebhookChannelSettingsDto
        {
            Enabled = source.Wecom.Enabled,
            WebhookUrl = string.IsNullOrEmpty(source.Wecom.WebhookUrl) ? null : NotificationSecretMask.Unchanged
        },
        Dingtalk = new WebhookChannelSettingsDto
        {
            Enabled = source.Dingtalk.Enabled,
            WebhookUrl = string.IsNullOrEmpty(source.Dingtalk.WebhookUrl) ? null : NotificationSecretMask.Unchanged
        }
    };

    private static void Validate(NotificationSettingsDto request)
    {
        if (request.Email.Enabled)
        {
            var recipients = request.Email.Recipients
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList();
            if (recipients.Count == 0)
                throw new ValidationFailedException("启用邮件渠道时至少需要一个收件人");
            if (recipients.Count > 20)
                throw new ValidationFailedException("收件人不能超过 20 个");
            if (recipients.Any(r => !r.Contains('@')))
                throw new ValidationFailedException("收件人必须为邮箱地址");
            if (string.IsNullOrWhiteSpace(request.Email.SmtpHost))
                throw new ValidationFailedException("启用邮件渠道时 smtpHost 必填");
        }

        if (request.Wecom.Enabled && string.IsNullOrWhiteSpace(request.Wecom.WebhookUrl))
            throw new ValidationFailedException("启用企业微信渠道时 webhookUrl 必填");
        if (request.Dingtalk.Enabled && string.IsNullOrWhiteSpace(request.Dingtalk.WebhookUrl))
            throw new ValidationFailedException("启用钉钉渠道时 webhookUrl 必填");
    }
}
