using System.Text;
using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 每日摘要推送（整改清单 2026-09-10 · R8）。
///
/// 报表在此之前全是拉取式（AdminReportController 清一色 HttpGet），没有任何定时推送。
/// 对备份产品来说日报比告警更重要：**告警只在坏的时候响，日报证明这套系统本身还活着**。
/// 一个星期没收到任何告警，可能是一切正常，也可能是服务端三天前就死了——
/// 这两件事在收件人那里长得一模一样，而日报是唯一能把它们分开的东西。
///
/// 三条刻意的设计：
/// 1. **「一切正常」也要发**。只在有事时发的日报就退化成了另一种告警，
///    而它要证明的恰恰是「今天这套系统还在运转」。
/// 2. **不受静默规则影响**。静默是给「维护窗口内某台机器刷屏」用的，不是用来关掉日报的。
///    实现上它天然如此：日报不经过 AlertingService，静默判定在那里面。
/// 3. 复用 notification_deliveries 通道，不另起投递链路——重试、失败告警、
///    渠道配置解密全都已经在 NotificationDispatchWorker 里，再写一份必然分家。
///
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁），幂等：
/// 同一个「昨天」只会产生一批投递，靠 last_digest_date 这个 system_settings 键守住。
/// </summary>
public class DailyDigestWorker : BackgroundService
{
    /// <summary>是否启用日报。</summary>
    public const string EnabledKey = "daily_digest_enabled";

    /// <summary>按报表时区的发送小时（0~23）。</summary>
    public const string HourKey = "daily_digest_hour";

    /// <summary>
    /// 已经发过日报的最后一个业务日（报表时区的 yyyy-MM-dd）。
    ///
    /// 幂等键放在库里而不是进程内存里：服务端重启是常事，
    /// 内存标记会让「今天 8 点重启一次」变成「今天收到两封日报」。
    /// </summary>
    public const string LastSentDateKey = "daily_digest_last_date";

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:daily_digest";

    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 5 分钟一轮。日报是「每天某个小时之后发一次」，不是「准点发」——
    /// 轮询密到分钟级只是给库多加无谓的查询，而晚五分钟没有人会察觉。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DailyDigestWorker> _logger;

    public DailyDigestWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DailyDigestWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("每日摘要工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scheduledLock = scope.ServiceProvider.GetRequiredService<IScheduledLockService>();
                if (await scheduledLock.TryAcquireAsync(LockKey, LockTtl, stoppingToken))
                {
                    try
                    {
                        await RunOnceAsync(scope, stoppingToken);
                    }
                    finally
                    {
                        await scheduledLock.ReleaseAsync(LockKey, CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "每日摘要轮询异常");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>公开是为了能单测：日报的价值全在「该发的时候真的发了」，那必须钉住。</summary>
    public async Task RunOnceAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();

        if (!await settings.GetBoolAsync(EnabledKey, true, ct))
            return;

        var timezone = PlanSchedule.ResolveTimeZone(_configuration["Reports:Timezone"]);
        var sendHour = Math.Clamp(await settings.GetIntAsync(HourKey, 8, ct), 0, 23);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone);

        if (nowLocal.Hour < sendHour)
            return;

        // 「昨天」按报表时区算。用 UTC 日期会让 UTC+8 的现场在早上 8 点收到一封
        // 掐在昨天 08:00 到今天 08:00 之间的报告，而人心里的「昨天」是自然日。
        var businessDate = nowLocal.Date.AddDays(-1);
        var stamp = businessDate.ToString("yyyy-MM-dd");

        var lastSent = await settings.GetStringAsync(LastSentDateKey, ct);
        if (string.Equals(lastSent, stamp, StringComparison.Ordinal))
            return;

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(businessDate, DateTimeKind.Unspecified), timezone);
        var toUtc = fromUtc.AddDays(1);

        var digest = await BuildDigestAsync(db, fromUtc, toUtc, ct);
        var created = await EnqueueDeliveriesAsync(scope, db, businessDate, timezone, digest, ct);

        // 无论有没有渠道可发，都要记下这一天已经处理过——否则没配渠道的系统
        // 每 5 分钟重算一次昨天的全部统计，白烧数据库。
        await MarkSentAsync(db, settings, stamp, ct);

        _logger.LogInformation(
            "每日摘要（{Date}）已生成：成功 {Success} 失败 {Failed} 未按计划 {Missed} 离线 {Offline}，投递 {Deliveries} 条",
            stamp, digest.SuccessCount, digest.FailedCount, digest.MissedCount, digest.OfflineClients, created);
    }

    /// <summary>昨日一天的执行情况。</summary>
    public sealed record DigestData(
        int SuccessCount,
        long SuccessBytes,
        int SuspiciousCount,
        int FailedCount,
        int MissedCount,
        int OfflineClients,
        int OpenCriticalAlerts);

    private static async Task<DigestData> BuildDigestAsync(
        AppDbContext db, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var arrived = await db.BackupSets.AsNoTracking()
            .Where(b => b.UploadedAt >= fromUtc && b.UploadedAt < toUtc
                        && BackupSetStatuses.Live.Contains(b.Status))
            .Select(b => new { b.TotalBytes, b.SizeSuspicious })
            .ToListAsync(ct);

        var failed = await db.UploadSessions.AsNoTracking()
            .CountAsync(s => s.Status == UploadStatus.Failed
                             && s.UpdatedAt >= fromUtc && s.UpdatedAt < toUtc, ct);

        // 「未按计划」取当天首次出现的 backup_missed 告警条数，与 MissedBackupWorker 同一口径。
        // 不自己重算一遍计划：两处各算各的，迟早在某个时区或宽限期上对不上，
        // 而日报和告警中心对不上比日报本身没有更糟。
        var missed = await db.Alerts.AsNoTracking()
            .CountAsync(a => a.Category == "backup_missed"
                             && a.FirstOccurredAt >= fromUtc && a.FirstOccurredAt < toUtc, ct);

        var offline = await db.Clients.AsNoTracking()
            .CountAsync(c => c.Status == ClientStatus.Offline, ct);

        var openCritical = await db.Alerts.AsNoTracking()
            .CountAsync(a => a.Level == AlertLevel.Critical
                             && (a.Status == AlertStatus.Open
                                 || a.Status == AlertStatus.Acknowledged
                                 || a.Status == AlertStatus.InProgress), ct);

        return new DigestData(
            arrived.Count,
            arrived.Sum(b => b.TotalBytes),
            arrived.Count(b => b.SizeSuspicious),
            failed,
            missed,
            offline,
            openCritical);
    }

    /// <summary>
    /// 按当前启用的渠道排队投递。渠道一个都没配时不产生投递——
    /// 那种情况下要看见的是概览页那条常驻横幅（R5），不是往库里堆一批永远发不出去的记录。
    /// </summary>
    private static async Task<int> EnqueueDeliveriesAsync(
        IServiceScope scope,
        AppDbContext db,
        DateTime businessDate,
        TimeZoneInfo timezone,
        DigestData digest,
        CancellationToken ct)
    {
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var channels = await notifications.GetSettingsAsync(ct);

        var subject = $"{businessDate:yyyy-MM-dd} 备份日报" + SubjectSuffixFor(digest);
        var body = BuildBody(businessDate, timezone, digest);

        // 不传类别：日报刻意不可按类别关掉。它要证明的恰恰是「这套系统今天还活着」，
        // 关掉之后「一切正常」和「服务端三天前就死了」在收件人那里长得一模一样。
        var created = NotificationNotice.Enqueue(db, channels, subject, body);

        if (created > 0)
            await db.SaveChangesAsync(ct);

        return created;
    }

    /// <summary>
    /// 标题后缀。公开是为了能单测「一切正常也要发」那一支——
    /// 那是这条整改的核心，而它在共享测试库上没法靠真实数据构造出来。
    /// </summary>
    public static string SubjectSuffixFor(DigestData digest) =>
        IsAllClear(digest) ? "：一切正常" : $"：{Trouble(digest)}";

    private static bool IsAllClear(DigestData d) =>
        d.FailedCount == 0 && d.MissedCount == 0 && d.OfflineClients == 0 && d.SuspiciousCount == 0;

    private static string Trouble(DigestData d)
    {
        var parts = new List<string>();
        if (d.MissedCount > 0) parts.Add($"{d.MissedCount} 项未按计划");
        if (d.FailedCount > 0) parts.Add($"{d.FailedCount} 次传输失败");
        if (d.SuspiciousCount > 0) parts.Add($"{d.SuspiciousCount} 份大小可疑");
        if (d.OfflineClients > 0) parts.Add($"{d.OfflineClients} 台离线");
        return string.Join("、", parts);
    }

    private static string BuildBody(DateTime businessDate, TimeZoneInfo timezone, DigestData d)
    {
        var body = new StringBuilder()
            .AppendLine($"业务日：{businessDate:yyyy-MM-dd}（{timezone.Id}）")
            .AppendLine()
            .AppendLine($"成功入库：{d.SuccessCount} 份，共 {FormatBytes(d.SuccessBytes)}")
            .AppendLine($"大小可疑：{d.SuspiciousCount} 份")
            .AppendLine($"传输失败：{d.FailedCount} 次")
            .AppendLine($"未按计划产生备份：{d.MissedCount} 项")
            .AppendLine($"当前离线客户端：{d.OfflineClients} 台")
            .AppendLine($"当前未处理的严重告警：{d.OpenCriticalAlerts} 条")
            .AppendLine();

        if (IsAllClear(d))
        {
            body.AppendLine("昨天没有发现问题。");
        }
        else
        {
            if (d.SuspiciousCount > 0)
                body.AppendLine("「大小可疑」的备份**已经正常入库**，只是比历史基线小得反常，请到备份列表里确认它们是否完整。");
            if (d.MissedCount > 0)
                body.AppendLine("「未按计划」表示到了计划时刻仍然没有收到新备份，请到告警中心查看是哪些任务。");
        }

        // 这一句是日报存在的理由，必须写在正文里：
        // 收件人要能靠「有没有收到这封信」判断服务端本身是不是还活着。
        body.AppendLine()
            .AppendLine("这封日报每天固定发送。连续收不到它，说明服务端本身可能已经停了——");
        body.AppendLine("那是这套系统唯一无法自己报出来的故障。");

        return body.ToString();
    }

    private static async Task MarkSentAsync(
        AppDbContext db, SystemSettingsProvider settings, string stamp, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
            VALUES ({LastSentDateKey}, to_jsonb({stamp}::text), false, now())
            ON CONFLICT (setting_key) DO UPDATE
                SET setting_value = EXCLUDED.setting_value, updated_at = now()
            """, ct);
        settings.Invalidate();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
