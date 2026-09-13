using System.Text;
using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Shared.Models.Admin;
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
    /// 默认发送时刻。9 点：等早上的备份计划都跑完，快报里的数字反映的是备份之后的状态，
    /// 也不会和计划完成回执挤在同一分钟里。
    /// </summary>
    public const int DefaultHour = 9;

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

        // 默认关：这封信的价值取决于内容密度，而那取决于现场的备份节奏。
        // 由人在界面上打开，比默认发出去再让人来关更合适。
        if (!await settings.GetBoolAsync(EnabledKey, false, ct))
            return;

        var timezone = PlanSchedule.ResolveTimeZone(_configuration["Reports:Timezone"]);
        var sendHour = Math.Clamp(await settings.GetIntAsync(HourKey, DefaultHour, ct), 0, 23);
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
        digest = await WithClientHealthAsync(scope, digest, ct);
        digest = await WithEndpointHealthAsync(scope, digest, ct);
        var created = await EnqueueDeliveriesAsync(scope, db, businessDate, timezone, digest, ct);

        // 无论有没有渠道可发，都要记下这一天已经处理过——否则没配渠道的系统
        // 每 5 分钟重算一次昨天的全部统计，白烧数据库。
        await MarkSentAsync(db, settings, stamp, ct);

        _logger.LogInformation(
            "每日摘要（{Date}）已生成：成功 {Success} 失败 {Failed} 未按计划 {Missed} 离线 {Offline} 探测不通 {EndpointsDown}/{Endpoints}，投递 {Deliveries} 条",
            stamp, digest.SuccessCount, digest.FailedCount, digest.MissedCount, digest.OfflineClients,
            digest.Endpoints.Count(IsDown), digest.Endpoints.Count, created);
    }

    /// <summary>
    /// 一台客户端的健康快照。
    ///
    /// 这几个数字告警也在看，但告警是**踩线才响**：内存从 78% 慢慢爬到 92%、
    /// 源盘从 49% 慢慢降到 15%，这个过程告警一声不吭，只在踩线那天炸一下——
    /// 而炸的时候往往已经没有从容处理的余地了。每天一张快照，看的就是这个过程。
    /// </summary>
    public sealed record ClientHealth(
        string Name,
        bool Online,
        decimal? CpuPercent,
        decimal? MemoryPercent,
        decimal? DiskFreePercent,
        string? DiskName,
        int ActiveAlerts);

    /// <summary>资源告警阈值，用来给快报里的数字打 ⚠。与告警用的是同一组配置。</summary>
    public sealed record HealthThresholds(int CpuPercent, int MemoryPercent, int DiskFreePercent);

    /// <summary>
    /// 一条业务探测此刻的样子。
    ///
    /// 和客户端健康同一个性质：**此刻的快照**，不是昨天的回顾。
    /// 差别在于它回答的问题不一样——客户端健康说的是「这台机器还有没有余量」，
    /// 探测说的是「这套业务现在还用不用得了」，而后者才是现场最先被问到的那一句。
    ///
    /// 字段比界面上多，是因为快报没有「点进去看」这一步：
    /// 阈值、连续失败次数、多久没通过、失败原因，全都得在这一屏里写完，
    /// 否则人还是得开电脑，而那时候他多半就不看了。
    /// </summary>
    public sealed record EndpointHealth(
        string Name,
        string? ClientName,
        string Target,
        string? Status,
        int? LatencyMs,
        int ConsecutiveFailures,
        int FailureThreshold,
        int IntervalSeconds,
        string? Error,
        DateTime? LastProbedAtUtc,
        DateTime? LastSuccessAtUtc);

    /// <summary>昨日一天的执行情况，以及此刻每台客户端的健康度与每条业务探测的状态。</summary>
    public sealed record DigestData(
        int SuccessCount,
        long SuccessBytes,
        int SuspiciousCount,
        int FailedCount,
        int MissedCount,
        int OfflineClients,
        int OpenCriticalAlerts,
        IReadOnlyList<ClientHealth> Clients,
        HealthThresholds Thresholds,
        IReadOnlyList<EndpointHealth> Endpoints,
        int DisabledEndpoints,
        int EndpointOutages);

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

        // 昨天业务探测报了几次。取告警条数而不是自己数探测失败：
        // 一次故障里几十轮探测全失败，数探测得出「昨天失败 847 次」，那个数字没有意义。
        // 告警是按「一件事」合并过的，「昨天业务中断 2 次」才是人要的那个量。
        var endpointOutages = await db.Alerts.AsNoTracking()
            .CountAsync(a => a.Category == AlertCategoryCatalog.EndpointDown
                             && a.FirstOccurredAt >= fromUtc && a.FirstOccurredAt < toUtc, ct);

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
            openCritical,
            [],
            new HealthThresholds(85, 90, 10),
            [],
            0,
            endpointOutages);
    }

    /// <summary>
    /// 每台客户端此刻的健康度。
    ///
    /// 复用概览页那份资源快照，不自己再查一遍：两处各算各的，迟早在某个口径上对不上，
    /// 而「快报上说内存 78%、概览页上说 92%」比两边都没有更糟。
    /// </summary>
    private async Task<DigestData> WithClientHealthAsync(
        IServiceScope scope, DigestData digest, CancellationToken ct)
    {
        try
        {
            return await LoadClientHealthAsync(scope, digest, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取不到健康快照，信照发。这封信最不可替代的作用是「收到了 = 服务端还活着」，
            // 为了一张表把整封信拖没，等于把那个信号也一起丢了。
            _logger.LogWarning(ex, "健康快报：客户端健康快照取失败，本次只发备份部分");
            return digest;
        }
    }

    private static async Task<DigestData> LoadClientHealthAsync(
        IServiceScope scope, DigestData digest, CancellationToken ct)
    {
        var clients = await scope.ServiceProvider.GetRequiredService<IClientAdminService>()
            .GetResourceOverviewAsync(200, ct);

        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        var thresholds = new HealthThresholds(
            Math.Clamp(await settings.GetIntAsync("client_cpu_alert_percent", 85, ct), 1, 100),
            Math.Clamp(await settings.GetIntAsync("client_memory_alert_percent", 90, ct), 1, 100),
            Math.Clamp(await settings.GetIntAsync("client_disk_free_alert_percent", 10, ct), 1, 99));

        var rows = clients
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCulture)
            .Select(c => new ClientHealth(
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.Hostname : c.DisplayName,
                c.Status == "online",
                c.CpuPercent,
                c.MemoryPercent,
                c.MinSourceDiskFreePercent,
                c.MinSourceDiskName,
                c.ActiveAlertCount))
            .ToList();

        return digest with { Clients = rows, Thresholds = thresholds };
    }

    /// <summary>
    /// 每条业务探测此刻的状态。
    ///
    /// 和客户端健康一样，取不到就只发别的部分：这封信最不可替代的作用是
    /// 「收到了 = 服务端还活着」，为了一张表把整封信拖没，等于把那个信号也一起丢了。
    /// </summary>
    private async Task<DigestData> WithEndpointHealthAsync(
        IServiceScope scope, DigestData digest, CancellationToken ct)
    {
        try
        {
            return await LoadEndpointHealthAsync(scope, digest, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "健康快报：业务探测快照取失败，本次不带探测部分");
            return digest;
        }
    }

    private static async Task<DigestData> LoadEndpointHealthAsync(
        IServiceScope scope, DigestData digest, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 连客户端名一起取：一条叫「加密服务」的探测，光看名字不知道是哪台机器上的，
        // 而快报上人要的下一个动作就是「去那台机器上看」。
        var rows = await db.MonitoredEndpoints.AsNoTracking()
            .Select(e => new { Endpoint = e, ClientName = e.Client!.DisplayName })
            .ToListAsync(ct);

        // 停用的不列，只报个数。不列是因为那是人自己关的，天天列出来只会变成噪声；
        // 报个数是因为「我明明配了探测，快报里怎么没有」这个疑问必须有地方回答。
        var disabled = rows.Count(r => !r.Endpoint.Enabled);

        var endpoints = rows
            .Where(r => r.Endpoint.Enabled)
            .Select(r => new EndpointHealth(
                r.Endpoint.Name,
                string.IsNullOrWhiteSpace(r.ClientName) ? null : r.ClientName,
                EndpointProbeWorker.DescribeTarget(r.Endpoint),
                r.Endpoint.LastStatus,
                r.Endpoint.LastLatencyMs,
                r.Endpoint.ConsecutiveFailures,
                r.Endpoint.FailureThreshold,
                r.Endpoint.IntervalSeconds,
                r.Endpoint.LastError,
                r.Endpoint.LastProbedAt,
                r.Endpoint.LastSuccessAt))
            // 不通的排最前，然后是在抖的、还没探过的，正常的垫底。
            // 正常的那几行不是废话——它们说明网络是通的，坏的是上面跑的业务，
            // 而这个区分决定了人接下来是去重启服务还是去看网络。
            .OrderBy(e => IsDown(e) ? 0 : IsFlapping(e) ? 1 : e.Status is null ? 2 : 3)
            .ThenBy(e => e.ClientName, StringComparer.CurrentCulture)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .ToList();

        return digest with { Endpoints = endpoints, DisabledEndpoints = disabled };
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

        var subject = $"{businessDate:yyyy-MM-dd} 客户端健康" + SubjectSuffixFor(digest);
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
        d.FailedCount == 0 && d.MissedCount == 0 && d.OfflineClients == 0 && d.SuspiciousCount == 0
        && d.Clients.Count(c => IsStrained(c, d.Thresholds)) == 0
        && !d.Endpoints.Any(IsDown)
        && !AnyStale(d.Endpoints);

    /// <summary>这台机器有没有踩到资源阈值。踩了就在快报里打 ⚠，并写进标题。</summary>
    private static bool IsStrained(ClientHealth c, HealthThresholds t) =>
        c.CpuPercent >= t.CpuPercent
        || c.MemoryPercent >= t.MemoryPercent
        || (c.DiskFreePercent is not null && c.DiskFreePercent <= t.DiskFreePercent);

    /// <summary>
    /// 这条探测算不算「不通」。
    ///
    /// 门槛和告警同一个口径（连续失败到它自己的阈值），但**不看 AlertOnFailure**：
    /// 那个开关管的是「要不要在群里炸一条」，而快报是一份清单，
    /// 关掉告警的那条照样得出现在上面——不然它就成了一条谁也看不见的探测。
    /// </summary>
    private static bool IsDown(EndpointHealth e) =>
        e.Status == "down" && e.ConsecutiveFailures >= Math.Clamp(e.FailureThreshold, 1, 100);

    /// <summary>
    /// 刚开始失败、还没到阈值。
    ///
    /// 单拎出来是因为它既不能算「不通」（一次抖动就报，练几次人就麻木了），
    /// 也不该当没发生过——昨晚开始抖、今早还在抖，那通常就是塌之前的样子。
    /// </summary>
    private static bool IsFlapping(EndpointHealth e) =>
        e.Status == "down" && !IsDown(e);

    /// <summary>
    /// 这条探测自己已经很久没被探过了。
    ///
    /// 这是整块里最要紧的一种异常，而且它长得最像「正常」：
    /// 探测工作器停了之后，所有探测的 last_status 会永远停在最后一次的值上，
    /// 满屏的 ✓ 说的其实是「昨天下午三点的时候是通的」。
    /// 宽限给到 5 个间隔或 30 分钟，取大的——偶尔一轮卡住不该报。
    /// </summary>
    private static bool IsStale(EndpointHealth e) =>
        e.LastProbedAtUtc is not null
        && DateTime.UtcNow - e.LastProbedAtUtc.Value > TimeSpan.FromSeconds(
            Math.Max(1800, Math.Clamp(e.IntervalSeconds, 10, 86400) * 5));

    private static bool AnyStale(IReadOnlyList<EndpointHealth> endpoints) => endpoints.Any(IsStale);

    private static string Trouble(DigestData d)
    {
        var parts = new List<string>();

        // 业务不通排最前。「3 台资源吃紧」可以明天再说，
        // 「U8 用不了」是现在就有人在打电话——标题会被截断，最该活下来的是这一条。
        var down = d.Endpoints.Count(IsDown);
        if (down > 0) parts.Add($"{down} 项业务不通");

        // 紧随其后：探测停了意味着上面那个数字本身不可信。
        if (AnyStale(d.Endpoints)) parts.Add("业务探测已停");

        if (d.MissedCount > 0) parts.Add($"{d.MissedCount} 项未按计划");
        if (d.FailedCount > 0) parts.Add($"{d.FailedCount} 次传输失败");
        if (d.SuspiciousCount > 0) parts.Add($"{d.SuspiciousCount} 份大小可疑");
        if (d.OfflineClients > 0) parts.Add($"{d.OfflineClients} 台离线");

        var strained = d.Clients.Count(c => IsStrained(c, d.Thresholds));
        if (strained > 0) parts.Add($"{strained} 台资源吃紧");

        return string.Join("、", parts);
    }

    /// <summary>
    /// 快报正文。公开是为了能单测——尤其是业务探测那一块：
    /// 它写的是「不通多久了、为什么不通」，而这两样写错了不会报错，
    /// 只会让人每天读到一份看着很详细、其实指错方向的清单。
    /// </summary>
    public static string BuildBody(DateTime businessDate, TimeZoneInfo timezone, DigestData d)
    {
        var body = new StringBuilder();

        // 客户端健康排在最前：这是这封信每天都有新内容的那一部分。
        // 备份数字放后面且能省则省——一周备一两次的现场，天天写「0 份入库」
        // 会让人养成划过去的习惯，而那个习惯会连带把真正要紧的行一起划走。
        foreach (var c in d.Clients)
            body.AppendLine(FormatClient(c, d.Thresholds));

        if (d.Clients.Count > 0)
            body.AppendLine();

        AppendEndpoints(body, d);

        if (d.EndpointOutages > 0)
            body.AppendLine($"昨日业务中断：{d.EndpointOutages} 次");

        if (d.SuccessCount > 0)
            body.AppendLine($"昨日入库：{d.SuccessCount} 份，共 {FormatBytes(d.SuccessBytes)}（{businessDate:MM-dd}，{timezone.Id}）");

        if (d.SuspiciousCount > 0)
            body.AppendLine($"大小可疑：{d.SuspiciousCount} 份");
        if (d.FailedCount > 0)
            body.AppendLine($"传输失败：{d.FailedCount} 次");
        if (d.MissedCount > 0)
            body.AppendLine($"未按计划产生备份：{d.MissedCount} 项");

        body.AppendLine($"未处理的严重告警：{d.OpenCriticalAlerts} 条")
            .AppendLine();

        if (IsAllClear(d))
        {
            body.AppendLine("没有发现问题。");
        }
        else
        {
            if (d.SuspiciousCount > 0)
                body.AppendLine("「大小可疑」的备份**已经正常入库**，只是比历史基线小得反常，请到备份列表里确认它们是否完整。");
            if (d.MissedCount > 0)
                body.AppendLine("「未按计划」表示到了计划时刻仍然没有收到新备份，请到告警中心查看是哪些任务。");
            if (d.Endpoints.Any(IsDown))
                body.AppendLine("✗ 的那几项是**业务本身连不上**，不是备份失败——进程可能还好好的，但用户已经用不了了。");
            if (AnyStale(d.Endpoints))
                body.AppendLine("探测已经很久没有跑了，上面的探测状态是旧的，不能当作「现在是通的」来看。");
        }

        // 这一句是日报存在的理由，必须写在正文里：
        // 收件人要能靠「有没有收到这封信」判断服务端本身是不是还活着。
        body.AppendLine()
            .AppendLine("这封快报每天固定发送。连续收不到它，说明服务端本身可能已经停了——");
        body.AppendLine("那是这套系统唯一无法自己报出来的故障。");

        return body.ToString();
    }

    /// <summary>
    /// 快报里的一行客户端。
    ///
    /// 踩到阈值的数字后面打 ⚠：平时这封信干干净净，一旦有东西开始不对，
    /// 眼睛会自己被那个符号抓住，而不需要人逐个数字去比。
    /// </summary>
    private static string FormatClient(ClientHealth c, HealthThresholds t)
    {
        if (!c.Online)
            return $"{c.Name}  离线 ⚠";

        var parts = new List<string>
        {
            $"CPU {Percent(c.CpuPercent)}{Mark(c.CpuPercent >= t.CpuPercent)}",
            $"内存 {Percent(c.MemoryPercent)}{Mark(c.MemoryPercent >= t.MemoryPercent)}"
        };

        parts.Add(c.DiskFreePercent is null
            ? "无源盘"
            : $"{c.DiskName}可用 {Percent(c.DiskFreePercent)}"
              + Mark(c.DiskFreePercent <= t.DiskFreePercent));

        if (c.ActiveAlerts > 0)
            parts.Add($"{c.ActiveAlerts} 条告警");

        return $"{c.Name}  在线  {string.Join("  ", parts)}";
    }

    /// <summary>
    /// 业务探测那一块。
    ///
    /// 排在客户端健康之后、昨日备份之前：它回答的是「现在还能不能用」，
    /// 比「昨天备了几份」更靠近人打开这封信时心里的那个问题。
    ///
    /// 不通的那几条写两行（第二行是原因），正常的一行带上耗时。
    /// 耗时对正常的那些不是装饰——从 200 毫秒变成 8 秒是最早的预警，
    /// 而那个时候状态码还是 200，告警一声不吭。
    /// </summary>
    private static void AppendEndpoints(StringBuilder body, DigestData d)
    {
        if (d.Endpoints.Count == 0)
        {
            // 一条探测都没配时整块不出现。这里刻意不写「未配置业务探测」去劝人来配：
            // 快报是拿来看状态的，不是拿来推销功能的。
            if (d.DisabledEndpoints > 0)
                body.AppendLine($"业务探测：{d.DisabledEndpoints} 项全部已停用").AppendLine();
            return;
        }

        var down = d.Endpoints.Count(IsDown);
        var flapping = d.Endpoints.Count(IsFlapping);
        var never = d.Endpoints.Count(e => e.Status is null);
        var ok = d.Endpoints.Count - down - flapping - never;

        var summary = new List<string> { $"{ok} 项正常" };
        if (down > 0) summary.Add($"{down} 项不通");
        if (flapping > 0) summary.Add($"{flapping} 项在抖");
        if (never > 0) summary.Add($"{never} 项尚未探测");
        if (d.DisabledEndpoints > 0) summary.Add($"{d.DisabledEndpoints} 项已停用");

        body.AppendLine($"业务探测：{string.Join("，", summary)}");

        // 这一句要在清单之前：清单上满屏的 ✓ 说的是「最后一次探的时候是通的」，
        // 人得先知道那是什么时候的事，再去看下面每一行。
        if (AnyStale(d.Endpoints))
        {
            var latest = d.Endpoints.Where(e => e.LastProbedAtUtc is not null)
                .Max(e => e.LastProbedAtUtc);
            body.AppendLine(latest is null
                ? "⚠ 探测已停：下面的状态都是旧的。"
                : $"⚠ 探测已停：最近一次探测在 {Ago(latest.Value)}前，下面的状态都是那时候的。");
        }

        foreach (var e in d.Endpoints)
            AppendEndpoint(body, e);

        body.AppendLine();
    }

    private static void AppendEndpoint(StringBuilder body, EndpointHealth e)
    {
        var who = e.ClientName is null ? e.Name : $"{e.Name} · {e.ClientName}";

        if (e.Status is null)
        {
            body.AppendLine($"? {who}  {e.Target}  尚未探测");
            return;
        }

        if (e.Status != "down")
        {
            // 耗时带上：正常项里唯一会变化的就是这个数，而它变慢是塌之前最早能看见的信号。
            var latency = e.LatencyMs is null ? string.Empty : $"  {e.LatencyMs} ms";
            var mark = IsStale(e) ? "  ⚠ 状态已旧" : string.Empty;
            body.AppendLine($"✓ {who}  {e.Target}{latency}{mark}");
            return;
        }

        // 不通 / 在抖：第一行是「是什么、在哪、有多久」，第二行是原因原文。
        // 原因不概括——「状态码 200 正常，但响应里找不到「欢迎登录」」和
        // 「10 秒内没有响应」指向完全不同的两件事，概括掉就只剩「失败」了。
        // ✗ 是「已经不通了」，! 是「开始抖但还没到门槛」。
        // 两者刻意不同形：一眼扫过去，该现在处理的和该留意的不该长成一个样子。
        var head = IsDown(e) ? "✗" : "!";
        var howLong = e.LastSuccessAtUtc is null
            ? "从来没有通过过"
            : $"已 {Ago(e.LastSuccessAtUtc.Value)}没有通过";
        var threshold = IsDown(e)
            ? $"连续失败 {e.ConsecutiveFailures} 次"
            : $"连续失败 {e.ConsecutiveFailures} 次（还没到 {Math.Clamp(e.FailureThreshold, 1, 100)} 次的报警门槛）";

        body.AppendLine($"{head} {who}  {e.Target}");
        body.AppendLine($"    {threshold}，{howLong}");
        body.AppendLine($"    {e.Error ?? "失败"}");
    }

    /// <summary>「已 3 小时 12 分钟」这种说法。绝对时刻还要人心算，而这里要的就是那个差值。</summary>
    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;

        return d.TotalMinutes < 1 ? "不到 1 分钟"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} 分钟"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} 小时 {d.Minutes} 分钟"
            : $"{(int)d.TotalDays} 天 {d.Hours} 小时";
    }

    private static string Percent(decimal? value) =>
        value is null ? "—" : $"{Math.Round(value.Value)}%";

    private static string Mark(bool strained) => strained ? " ⚠" : string.Empty;

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
