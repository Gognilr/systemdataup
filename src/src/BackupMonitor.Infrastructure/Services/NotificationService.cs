using System.Text.Json;
using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
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
    Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken ct = default);
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
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        AppDbContext db,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _context = context;
        _audit = audit;
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
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SettingKey == SettingsKey, ct);

        if (row is null)
            return new NotificationSettingsDto();

        try
        {
            return JsonSerializer.Deserialize<NotificationSettingsDto>(row.SettingValue, JsonOpts)
                   ?? new NotificationSettingsDto();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "通知渠道配置 {Key} 反序列化失败，返回默认配置", SettingsKey);
            return new NotificationSettingsDto();
        }
    }

    public async Task<NotificationSettingsDto> UpdateSettingsAsync(NotificationSettingsDto request, CancellationToken ct = default)
    {
        Validate(request);

        var json = JsonSerializer.Serialize(request, JsonOpts);
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == SettingsKey, ct);

        if (row is null)
        {
            row = new SystemSetting
            {
                SettingKey = SettingsKey,
                SettingValue = json,
                Encrypted = false,
                UpdatedBy = _context.UserId
            };
            _db.SystemSettings.Add(row);
        }
        else
        {
            row.SettingValue = json;
            row.UpdatedBy = _context.UserId;
        }

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("notification.update_settings", AuditResult.Success, "system_setting", null,
            afterData: json, ct: ct);

        _logger.LogInformation("通知渠道配置已更新 email={Email} wecom={Wecom} dingtalk={Dingtalk}",
            request.Email.Enabled, request.Wecom.Enabled, request.Dingtalk.Enabled);

        return request;
    }

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
