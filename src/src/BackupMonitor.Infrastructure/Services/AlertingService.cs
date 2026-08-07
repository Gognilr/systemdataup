using System.Text.Json;
using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 告警触发服务（设计书 8.10 告警模块）：
/// 同一 alert_key 在活动状态（open/acknowledged/in_progress）下去重聚合，
/// 由数据库部分唯一索引 uq_alerts_active_key 兜底。
/// </summary>
public interface IAlertingService
{
    /// <summary>触发或聚合一条告警；恢复正常告警时调用 RecoverAsync</summary>
    Task RaiseAsync(
        string alertKey,
        AlertLevel level,
        string category,
        string title,
        string? message = null,
        Guid? clientId = null,
        Guid? taskId = null,
        Guid? businessUnitId = null,
        Guid? backupSetId = null,
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>按 alert_key 自动恢复活动告警（如服务恢复运行）</summary>
    Task RecoverAsync(string alertKey, CancellationToken ct = default);
}

/// <summary>告警触发实现</summary>
public class AlertingService : IAlertingService
{
    private static readonly AlertStatus[] ActiveStatuses =
        [AlertStatus.Open, AlertStatus.Acknowledged, AlertStatus.InProgress];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppDbContext _db;
    private readonly ILogger<AlertingService> _logger;

    public AlertingService(AppDbContext db, ILogger<AlertingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RaiseAsync(
        string alertKey,
        AlertLevel level,
        string category,
        string title,
        string? message = null,
        Guid? clientId = null,
        Guid? taskId = null,
        Guid? businessUnitId = null,
        Guid? backupSetId = null,
        string? metadata = null,
        CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;

            var existing = await _db.Alerts
                .Where(a => a.AlertKey == alertKey && ActiveStatuses.Contains(a.Status))
                .OrderByDescending(a => a.LastOccurredAt)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                existing.LastOccurredAt = now;
                existing.OccurrenceCount++;
                existing.Message = message ?? existing.Message;
                if (level < existing.Level)
                    existing.Level = level; // Critical=0 数值小即等级高
            }
            else
            {
                var alert = new Alert
                {
                    Id = Guid.NewGuid(),
                    AlertKey = alertKey,
                    Level = level,
                    Status = AlertStatus.Open,
                    Category = category,
                    ClientId = clientId,
                    TaskId = taskId,
                    BusinessUnitId = businessUnitId,
                    BackupSetId = backupSetId,
                    Title = title,
                    Message = message,
                    FirstOccurredAt = now,
                    LastOccurredAt = now,
                    OccurrenceCount = 1,
                    Metadata = metadata
                };
                _db.Alerts.Add(alert);

                // 新告警按渠道配置落待发送通知记录（第二批补充设计；实际发送由发送器实现）
                await CreateDeliveriesForAsync(alert, ct);
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // 并发下部分唯一索引冲突：另一请求已插入同键活动告警，忽略即可
            _logger.LogDebug("告警 {AlertKey} 并发去重冲突，忽略", alertKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "触发告警失败 alertKey={AlertKey}", alertKey);
        }
    }

    public async Task RecoverAsync(string alertKey, CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            await _db.Alerts
                .Where(a => a.AlertKey == alertKey && ActiveStatuses.Contains(a.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AlertStatus.Recovered)
                    .SetProperty(a => a.RecoveredAt, now), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复告警失败 alertKey={AlertKey}", alertKey);
        }
    }

    /// <summary>为新告警生成待发送通知记录（渠道配置见 system_settings: notification_channels）</summary>
    private async Task CreateDeliveriesForAsync(Alert alert, CancellationToken ct)
    {
        try
        {
            var row = await _db.SystemSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingKey == NotificationService.SettingsKey, ct);
            if (row is null)
                return;

            var settings = JsonSerializer.Deserialize<NotificationSettingsDto>(row.SettingValue, JsonOptions);
            if (settings is null)
                return;

            if (settings.Email.Enabled)
            {
                foreach (var recipient in settings.Email.Recipients
                             .Where(r => !string.IsNullOrWhiteSpace(r))
                             .Distinct()
                             .Take(20))
                {
                    _db.NotificationDeliveries.Add(new NotificationDelivery
                    {
                        Id = Guid.NewGuid(),
                        Alert = alert,
                        Channel = NotificationChannel.Email,
                        Recipient = recipient.Trim(),
                        Status = NotificationStatus.Pending,
                        AttemptCount = 0
                    });
                }
            }

            if (settings.Wecom.Enabled && !string.IsNullOrWhiteSpace(settings.Wecom.WebhookUrl))
            {
                _db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    Id = Guid.NewGuid(),
                    Alert = alert,
                    Channel = NotificationChannel.Wecom,
                    Recipient = Truncate(settings.Wecom.WebhookUrl.Trim(), 255),
                    Status = NotificationStatus.Pending,
                    AttemptCount = 0
                });
            }

            if (settings.Dingtalk.Enabled && !string.IsNullOrWhiteSpace(settings.Dingtalk.WebhookUrl))
            {
                _db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    Id = Guid.NewGuid(),
                    Alert = alert,
                    Channel = NotificationChannel.Dingtalk,
                    Recipient = Truncate(settings.Dingtalk.WebhookUrl.Trim(), 255),
                    Status = NotificationStatus.Pending,
                    AttemptCount = 0
                });
            }
        }
        catch (Exception ex)
        {
            // 通知落单失败不得影响告警主流程
            _logger.LogWarning(ex, "创建通知发送记录失败 alertId={AlertId}", alert.Id);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
