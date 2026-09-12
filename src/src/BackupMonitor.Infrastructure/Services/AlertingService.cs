using System.Text.Json;
using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>长期未恢复的高等级告警多久重发一次通知（小时）system_settings 键（V033）</summary>
    public const string RenotifyHoursKey = "alert_renotify_hours";

    /// <summary>重发间隔的兜底默认值（小时）</summary>
    public const int DefaultRenotifyHours = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 告警刻意用**自己的**作用域与 DbContext，而不是调用方那一个。
    ///
    /// 原先注入的是同作用域的 AppDbContext，方法结尾直接 SaveChangesAsync。
    /// 调用方往往在方法中途已经改过实体、还没走到自己的提交点，这一次保存会把半成品
    /// 一起写进去；其中若有约束冲突，异常还会被下面那个 catch (DbUpdateException)
    /// 当成「告警去重冲突」吞掉——于是调用方的业务写入失败了，却没有任何人知道。
    ///
    /// 换成独立作用域之后，告警的提交与调用方的事务彻底分开，
    /// 那个 catch 也只会捕获告警自己的并发冲突。代价是告警不再随调用方的事务回滚，
    /// 这是想要的：一次失败的操作也应该留下它的告警。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly ILogger<AlertingService> _logger;

    public AlertingService(IServiceScopeFactory scopeFactory, ILogger<AlertingService> logger)
    {
        _scopeFactory = scopeFactory;
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
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var agentNotifications = scope.ServiceProvider.GetRequiredService<IAgentNotificationService>();

            // 这里原本有一道前置守卫：查不到同键的活动告警就直接 return。
            // 判断方向写反了——「不存在活动告警」恰恰是需要新建告警的情形，
            // 结果是下面创建新告警的 else 分支成为死代码，全系统任何 RaiseAsync 都不产生告警：
            // 入库失败、保留删除失败、回收熔断……监控的输出端整体失效。
            // 该守卫与下面的 existing 查询本就重复，直接去掉，由 existing 是否为 null 决定新建还是累加。
            var now = DateTime.UtcNow;

            var existing = await db.Alerts
                .Where(a => a.AlertKey == alertKey && ActiveStatuses.Contains(a.Status))
                .OrderByDescending(a => a.LastOccurredAt)
                .FirstOrDefaultAsync(ct);

            // 静默命中：维护窗口内一台机器持续产生告警时，只累加次数，
            // 不发通知、不推送 Agent 托盘——但告警本身仍要建/仍要计数，
            // 这样静默解除或到期后，历史发生次数是完整的（验收标准：计数在静默期内仍在累加）。
            var silenced = await db.AlertSilences
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
                    await CreateDeliveriesForAsync(db, existing, ct, titlePrefix: "【已升级】");
                    existing.LastNotifiedAt = now;

                    if (clientId is not null)
                    {
                        // 去重键带等级：Warning→Critical 推一次，
                        // 之后同等级的重复发生不再推（托盘不是用来刷屏的）。
                        await agentNotifications.EnqueueAsync(
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
                else if (!silenced && existing.Level <= AlertLevel.Warning)
                {
                    // D6：长期未恢复的高等级告警按间隔重发。
                    //
                    // 告警此前只有两个通知触发点：新建、等级提升。一条 Critical 挂在那里三天
                    // 没人处理，系统只在第一分钟发过一封邮件——之后完全沉默，
                    // 而「没有新邮件」在收件人那里读起来和「已经好了」是一样的。
                    // 只对 Critical / Warning 重发（Notice 级不值得反复打扰），
                    // 而且只在这条告警又发生了一次的时候（走到这个分支就说明它刚刚重现）。
                    var lastNotified = existing.LastNotifiedAt ?? existing.FirstOccurredAt;
                    var elapsed = now - lastNotified;

                    // 先用最短可能的间隔（下面 Clamp 的下限 1 小时）挡一道再去读配置。
                    // 这个分支是所有重复触发的必经之路——心跳级别的告警一分钟走好几次，
                    // 每次都去问一遍系统配置没有意义。
                    if (elapsed >= TimeSpan.FromHours(1))
                    {
                        var renotifyHours = Math.Clamp(
                            await scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>()
                                .GetIntAsync(RenotifyHoursKey, DefaultRenotifyHours, ct),
                            1, 720);

                        if (elapsed >= TimeSpan.FromHours(renotifyHours))
                        {
                            await CreateDeliveriesForAsync(db, existing, ct, titlePrefix: "【仍未恢复】");
                            existing.LastNotifiedAt = now;

                            _logger.LogInformation(
                                "告警 {AlertKey} 已持续 {Hours:0} 小时未恢复，重发一次通知",
                                alertKey, (now - existing.FirstOccurredAt).TotalHours);
                        }
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
                db.Alerts.Add(alert);

                if (!silenced)
                {
                    // 新告警按渠道配置落待发送通知记录（第二批补充设计；实际发送由发送器实现）
                    await CreateDeliveriesForAsync(db, alert, ct);
                    alert.LastNotifiedAt = now;

                    // Agent 只接收新告警与等级提升，活动告警后续心跳仅更新次数，不重复弹窗。
                    if (clientId is not null)
                    {
                        await agentNotifications.EnqueueAsync(
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

            await db.SaveChangesAsync(ct);
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

    /// <summary>
    /// 恢复通知的开关（system_settings 键），默认**开**。
    ///
    /// 「没有新消息」在收件人那里读起来和「已经好了」是一样的——而这两者差别很大。
    /// 收到一条明确的「已恢复」，人才知道可以不用管了；否则只能自己去翻管理页面确认。
    ///
    /// 留一个开关是因为它确实会让消息量接近翻倍：坏一次、好一次。
    /// 嫌吵可以关掉，关掉之后回到「只报坏消息」。
    /// </summary>
    public const string RecoveryNotifyKey = "alert_recovery_notify_enabled";

    /// <summary>
    /// 告警恢复了，给当初收到过坏消息的那些渠道补一条「已恢复」。
    ///
    /// 复用 CreateDeliveriesForAsync：渠道启用状态、等级筛选、排除类别全都跟着告警本身走，
    /// 于是「坏消息发到哪，好消息就发到哪」——不会出现钉钉收到恢复、邮箱没收到的错位。
    /// </summary>
    private async Task NotifyRecoveryAsync(
        IServiceScope scope, AppDbContext db, string alertKey,
        IReadOnlyList<Guid> recoveringIds, DateTime now, CancellationToken ct)
    {
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        if (!await settings.GetBoolAsync(RecoveryNotifyKey, true, ct))
            return;

        // 只给**真的收到过**坏消息的人发恢复。
        //
        // 判据是「至少有一条投递已经发出去了」，不是「这条告警建过投递」——
        // 抖动恢复时那封还躺在队列里就被上面取消了，没有任何人听说过它坏过，
        // 这时候突然来一条「已恢复」，收件人只会困惑：恢复什么？
        // 同理，被静默挡下、被渠道筛选滤掉、或者压根没配渠道的告警，也都不该有恢复通知。
        var notifiedIds = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.AlertId != null
                        && recoveringIds.Contains(d.AlertId.Value)
                        && d.Status == NotificationStatus.Sent)
            .Select(d => d.AlertId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (notifiedIds.Count == 0)
            return;

        // 静默期间不发恢复——维护窗口里机器起起落落是正常的，
        // 每恢复一次就响一声，静默就等于没静默。
        var silenced = await db.AlertSilences
            .AnyAsync(s => s.Until > now && EF.Functions.Like(alertKey, s.AlertKeyPattern), ct);
        if (silenced)
            return;

        var alerts = await db.Alerts
            .Where(a => notifiedIds.Contains(a.Id))
            .ToListAsync(ct);

        foreach (var alert in alerts)
            await CreateDeliveriesForAsync(db, alert, ct, titlePrefix: "【已恢复】");

        await db.SaveChangesAsync(ct);
    }

    public async Task RecoverAsync(string alertKey, CancellationToken ct = default)
    {
        try
        {
            // 与 RaiseAsync 同一个理由：恢复告警要走自己的 DbContext，
            // 不能借调用方那一个（见 _scopeFactory 的注释）。
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // D2：心跳每次「一切正常」都会调一次 RecoverAsync，绝大多数情况下根本没有活动告警可恢复。
            // 先查一次（alert_key 上有索引，只取 id 就是一次索引扫描），空集直接返回，
            // 省掉两条无谓的 UPDATE（不产生 WAL）。
            //
            // 审计 G-12：这里取的是 ID 列表而不是直接 ExecuteUpdate——
            // ExecuteUpdate 不支持跨表条件，而下面取消投递必须按 alert_id 定位。
            var now = DateTime.UtcNow;

            var recoveringIds = await db.Alerts
                .Where(a => a.AlertKey == alertKey && ActiveStatuses.Contains(a.Status))
                .Select(a => a.Id)
                .ToListAsync(ct);
            if (recoveringIds.Count == 0)
                return;

            await db.Alerts
                .Where(a => recoveringIds.Contains(a.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AlertStatus.Recovered)
                    .SetProperty(a => a.RecoveredAt, now), ct);

            // 审计 G-12：告警恢复了，还没发出去的通知就不该再发。
            // 派发器只看投递记录自身的状态，不回查告警——短暂抖动产生的邮件
            // 会在告警早已恢复之后继续发，重试间隔 5 分钟最多 3 次，一次抖动发三轮。
            await db.NotificationDeliveries
                .Where(d => d.AlertId != null
                            && recoveringIds.Contains(d.AlertId.Value)
                            && d.Status == NotificationStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, NotificationStatus.Cancelled), ct);

            await NotifyRecoveryAsync(scope, db, alertKey, recoveringIds, now, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复告警失败 alertKey={AlertKey}", alertKey);
        }
    }

    /// <summary>
    /// 这条告警该不该走这个渠道（R24）。
    ///
    /// 筛的是**投递**，不是告警本身：被筛掉的告警照样建、照样计数、照样出现在管理网页
    /// 和客户端托盘上——这里决定的只有「要不要为它单独打扰一次人」。
    /// 这个区别是这套筛选敢于默认挡掉 Notice 的前提。
    ///
    /// 空配置（老数据里没有这个字段）按 warning 处理：升级之后默认 Critical + Warning。
    /// </summary>
    internal static bool PassesFilter(NotificationFilterDto? filter, Alert alert)
    {
        // AlertLevel 的枚举顺序是 Critical=0 / Warning=1 / Notice=2，
        // 数值越小越严重。用显式的 severity 名次，免得将来往枚举里插一个值就把判定改了含义。
        static int Severity(AlertLevel level) => level switch
        {
            AlertLevel.Critical => 3,
            AlertLevel.Warning => 2,
            _ => 1
        };

        var minimum = (filter?.MinLevel ?? "warning").Trim().ToLowerInvariant() switch
        {
            "notice" => 1,
            "critical" => 3,
            _ => 2
        };

        if (Severity(alert.Level) < minimum)
            return false;

        return filter?.ExcludedCategories is not { Count: > 0 } excluded
               || string.IsNullOrWhiteSpace(alert.Category)
               || !excluded.Any(c => string.Equals(c?.Trim(), alert.Category, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>告警等级到托盘提示 severity 的映射</summary>
    private static string SeverityOf(AlertLevel level) => level switch
    {
        AlertLevel.Critical => "critical",
        AlertLevel.Warning => "warning",
        _ => "info"
    };

    /// <summary>
    /// webhook 渠道投递记录里的「收件人」。
    ///
    /// 这里刻意**不存 webhook 地址**。原因有两条，各自都足够：
    ///
    /// 一是这里读的是 system_settings 的原始 JSON（没走 INotificationService，它才是解密的那一层），
    /// 拿到的地址是整改批次 C·C1 加密后的密文 "enc:v1:…"。把它塞进 Recipient，投递时就会被
    /// 当成 URL 去请求，HttpClient 报 "The 'enc' scheme is not supported."——钉钉/企业微信
    /// 从来就没收到过任何东西。何况密文比 255 字符长，截断之后连解密都救不回来。
    ///
    /// 二是就算这里能拿到明文也不该存：webhook 地址本身就是凭据（含 access_token），
    /// 而投递页会把 Recipient 这一列原样渲染出来（js/views/notifications.js 的「收件人」列）。
    ///
    /// 真正发送时用的地址由投递工作器从解密后的渠道配置里现取，见
    /// <see cref="NotificationDispatchWorker"/>。顺带的好处是：改完 webhook 地址后，
    /// 早先排队的投递会用新地址重试，而不是抱着下发那一刻的旧快照。
    /// </summary>
    private const string WecomRecipientLabel = NotificationNotice.WecomRecipientLabel;

    /// <inheritdoc cref="WecomRecipientLabel"/>
    private const string DingtalkRecipientLabel = NotificationNotice.DingtalkRecipientLabel;

    /// <summary>
    /// 为告警生成待发送通知记录（渠道配置见 system_settings: notification_channels）。
    /// titlePrefix 供等级提升复用同一套渠道展开逻辑（审计 G-11）。
    /// </summary>
    private async Task CreateDeliveriesForAsync(AppDbContext db, Alert alert, CancellationToken ct, string? titlePrefix = null)
    {
        try
        {
            var row = await db.SystemSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingKey == NotificationService.SettingsKey, ct);
            if (row is null)
                return;

            var settings = JsonSerializer.Deserialize<NotificationSettingsDto>(row.SettingValue, JsonOptions);
            if (settings is null)
                return;

            if (settings.Email.Enabled && PassesFilter(settings.Email.Filter, alert))
            {
                foreach (var recipient in settings.Email.Recipients
                             .Where(r => !string.IsNullOrWhiteSpace(r))
                             .Distinct()
                             .Take(20))
                {
                    db.NotificationDeliveries.Add(new NotificationDelivery
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

            if (settings.Wecom.Enabled
                && !string.IsNullOrWhiteSpace(settings.Wecom.WebhookUrl)
                && PassesFilter(settings.Wecom.Filter, alert))
            {
                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    Id = Guid.NewGuid(),
                    Alert = alert,
                    Channel = NotificationChannel.Wecom,
                    Recipient = WecomRecipientLabel,
                    Status = NotificationStatus.Pending,
                    AttemptCount = 0,
                    TitlePrefix = titlePrefix
                });
            }

            if (settings.Dingtalk.Enabled
                && !string.IsNullOrWhiteSpace(settings.Dingtalk.WebhookUrl)
                && PassesFilter(settings.Dingtalk.Filter, alert))
            {
                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    Id = Guid.NewGuid(),
                    Alert = alert,
                    Channel = NotificationChannel.Dingtalk,
                    Recipient = DingtalkRecipientLabel,
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

}
