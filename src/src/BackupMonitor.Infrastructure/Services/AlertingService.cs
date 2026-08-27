using System.Text.Json;
using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
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
    private readonly IAgentNotificationService _agentNotifications;
    private readonly ILogger<AlertingService> _logger;

    public AlertingService(
        AppDbContext db,
        IAgentNotificationService agentNotifications,
        ILogger<AlertingService> logger)
    {
        _db = db;
        _agentNotifications = agentNotifications;
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
            // 这里原本有一道前置守卫：查不到同键的活动告警就直接 return。
            // 判断方向写反了——「不存在活动告警」恰恰是需要新建告警的情形，
            // 结果是下面创建新告警的 else 分支成为死代码，全系统任何 RaiseAsync 都不产生告警：
            // 入库失败、保留删除失败、回收熔断……监控的输出端整体失效。
            // 该守卫与下面的 existing 查询本就重复，直接去掉，由 existing 是否为 null 决定新建还是累加。
            var now = DateTime.UtcNow;

            var existing = await _db.Alerts
                .Where(a => a.AlertKey == alertKey && ActiveStatuses.Contains(a.Status))
                .OrderByDescending(a => a.LastOccurredAt)
                .FirstOrDefaultAsync(ct);

            // 静默命中：维护窗口内一台机器持续产生告警时，只累加次数，
            // 不发通知、不推送 Agent 托盘——但告警本身仍要建/仍要计数，
            // 这样静默解除或到期后，历史发生次数是完整的（验收标准：计数在静默期内仍在累加）。
            var silenced = await _db.AlertSilences
                .AnyAsync(s => s.Until > now && EF.Functions.Like(alertKey, s.AlertKeyPattern), ct);

            if (existing is not null)
            {
                existing.LastOccurredAt = now;
                existing.OccurrenceCount++;
                existing.Message = message ?? existing.Message;

                // 审计 G-11：等级提升是最该被推送的信号，原先却恰好是唯一不推送的。
                // 磁盘可用率从 14%（Warning）掉到 3%（Critical），等级在库里升上去了，
                // 但通知与托盘推送全在下面的 else 分支里，于是没有任何人收到消息——
                // 而唯一那封警告邮件可能是几天前发的、早被忽略了。
                var escalated = level < existing.Level;   // Critical=0，数值小即等级高
                if (escalated)
                    existing.Level = level;

                if (escalated && !silenced)
                {
                    // 标题带前缀，让收件人一眼看出这不是同一封警告的重发
                    await CreateDeliveriesForAsync(existing, ct, titlePrefix: "【已升级】");

                    if (clientId is not null)
                    {
                        // 去重键带等级：Warning→Critical 推一次，
                        // 之后同等级的重复发生不再推（托盘不是用来刷屏的）。
                        await _agentNotifications.EnqueueAsync(
                            clientId.Value,
                            "alert",
                            SeverityOf(level),
                            "【已升级】" + title,
                            message,
                            $"alert:{existing.Id}:escalated:{EnumMapping.ToSnakeCase(level)}",
                            existing.Id,
                            backupSetId,
                            ct);
                    }
                }
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

                if (!silenced)
                {
                    // 新告警按渠道配置落待发送通知记录（第二批补充设计；实际发送由发送器实现）
                    await CreateDeliveriesForAsync(alert, ct);

                    // Agent 只接收新告警与等级提升，活动告警后续心跳仅更新次数，不重复弹窗。
                    if (clientId is not null)
                    {
                        await _agentNotifications.EnqueueAsync(
                            clientId.Value,
                            "alert",
                            SeverityOf(level),
                            title,
                            message,
                            $"alert:{alert.Id}:opened",
                            alert.Id,
                            backupSetId,
                            ct);
                    }
                }
                else
                {
                    _logger.LogDebug("告警 {AlertKey} 命中静默规则，已建告警但不发通知/不推送托盘", alertKey);
                }
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
            // D2：心跳每次「一切正常」都会调一次 RecoverAsync，绝大多数情况下根本没有活动告警可恢复。
            // 先查一次（alert_key 上有索引，只取 id 就是一次索引扫描），空集直接返回，
            // 省掉两条无谓的 UPDATE（不产生 WAL）。
            //
            // 审计 G-12：这里取的是 ID 列表而不是直接 ExecuteUpdate——
            // ExecuteUpdate 不支持跨表条件，而下面取消投递必须按 alert_id 定位。
            var now = DateTime.UtcNow;
            var recoveringIds = await _db.Alerts
                .Where(a => a.AlertKey == alertKey && ActiveStatuses.Contains(a.Status))
                .Select(a => a.Id)
                .ToListAsync(ct);
            if (recoveringIds.Count == 0)
                return;

            await _db.Alerts
                .Where(a => recoveringIds.Contains(a.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AlertStatus.Recovered)
                    .SetProperty(a => a.RecoveredAt, now), ct);

            // 审计 G-12：告警恢复了，还没发出去的通知就不该再发。
            // 派发器只看投递记录自身的状态，不回查告警——短暂抖动产生的邮件
            // 会在告警早已恢复之后继续发，重试间隔 5 分钟最多 3 次，一次抖动发三轮。
            await _db.NotificationDeliveries
                .Where(d => d.AlertId != null
                            && recoveringIds.Contains(d.AlertId.Value)
                            && d.Status == NotificationStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, NotificationStatus.Cancelled), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复告警失败 alertKey={AlertKey}", alertKey);
        }
    }

    /// <summary>告警等级到托盘提示 severity 的映射</summary>
    private static string SeverityOf(AlertLevel level) => level switch
    {
        AlertLevel.Critical => "critical",
        AlertLevel.Warning => "warning",
        _ => "info"
    };

    /// <summary>
    /// 为告警生成待发送通知记录（渠道配置见 system_settings: notification_channels）。
    /// titlePrefix 供等级提升复用同一套渠道展开逻辑（审计 G-11）。
    /// </summary>
    private async Task CreateDeliveriesForAsync(Alert alert, CancellationToken ct, string? titlePrefix = null)
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
                        AttemptCount = 0,
                        TitlePrefix = titlePrefix
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
                    AttemptCount = 0,
                    TitlePrefix = titlePrefix
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
                    AttemptCount = 0,
                    TitlePrefix = titlePrefix
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
