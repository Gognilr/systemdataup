using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ClientEntity = BackupMonitor.Core.Entities.Client.Client;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 系统主动巡检工作器（整改 A1 + A4）。
///
/// 全系统原有的 13 处告警触发点无一例外挂在「Agent 报上来了什么」之后，
/// 于是最该报警的两件事——机器掉线、服务端磁盘要满——恰好都属于「什么都没发生」，
/// 永远不会被发现。这个 worker 补的就是这个缺口：不等任何人上报，自己巡检。
///
/// 阶段一（A1）：客户端存活判定。超过 client_suspected_offline_seconds 静默 → 疑似离线；
/// 超过 client_offline_threshold_seconds → 离线并发严重告警。心跳恢复时由
/// AgentHeartbeatService 负责 RecoverAsync。
/// 阶段二（A4）：服务端仓库/暂存磁盘可用比例自检，低于 server_storage_alert_percent 告警。
///
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁），幂等。
/// </summary>
public class SystemWatchdogWorker : BackgroundService
{
    /// <summary>执行间隔（秒）system_settings 键</summary>
    public const string IntervalKey = "client_liveness_interval_seconds";

    /// <summary>疑似离线阈值（秒）system_settings 键（V001 已种子，此前零引用）</summary>
    public const string SuspectedOfflineKey = "client_suspected_offline_seconds";

    /// <summary>离线阈值（秒）system_settings 键（V001 已种子，此前零引用）</summary>
    public const string OfflineThresholdKey = "client_offline_threshold_seconds";

    /// <summary>服务端磁盘可用比例告警阈值（百分比）system_settings 键</summary>
    public const string StorageAlertPercentKey = "server_storage_alert_percent";

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:system_watchdog";

    /// <summary>锁 TTL：须大于单轮最坏耗时，防止执行中被抢占重复执行</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);

    /// <summary>磁盘可用比例低于该值时告警升级为严重</summary>
    private const double StorageCriticalPercent = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemWatchdogWorker> _logger;

    public SystemWatchdogWorker(IServiceScopeFactory scopeFactory, ILogger<SystemWatchdogWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("系统主动巡检工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 30;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                intervalSeconds = Math.Max(5, await settings.GetIntAsync(IntervalKey, 30, stoppingToken));

                var scheduledLock = scope.ServiceProvider.GetRequiredService<IScheduledLockService>();
                if (await scheduledLock.TryAcquireAsync(LockKey, LockTtl, stoppingToken))
                {
                    try
                    {
                        await RunPassAsync(scope, stoppingToken);
                    }
                    finally
                    {
                        await scheduledLock.ReleaseAsync(LockKey, CancellationToken.None);
                    }
                }
                // 未抢到锁说明其他实例正在巡检，本轮跳过
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "系统主动巡检轮询异常");
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

    private async Task RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        await RunClientLivenessAsync(scope, ct);
        await RunStorageSelfCheckAsync(scope, ct);
        await RunConfigBackupOverdueCheckAsync(scope, ct);
        await RunCertificateLifecycleCheckAsync(scope, ct);
        await RunAgentVersionDriftCheckAsync(scope, ct);
    }

    /// <summary>
    /// R11：Agent 版本漂移。
    ///
    /// 一条聚合告警而不是每台一条：十台机器落后不是十件事，是一件事，
    /// 而且处置动作是同一个——去升级页面批量下发。
    /// 每台一条只会把告警中心刷满，然后没人再看它。
    /// </summary>
    private async Task RunAgentVersionDriftCheckAsync(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
            var versions = scope.ServiceProvider.GetRequiredService<IAgentVersionService>();
            var status = await versions.GetStatusAsync(ct);

            if (!status.Overdue)
            {
                await alerting.RecoverAsync(AgentVersionDriftAlertKey, ct);
                return;
            }

            var names = string.Join("、", status.OutdatedClients
                .Take(5)
                .Select(c => string.IsNullOrWhiteSpace(c.DisplayName) ? c.Hostname : c.DisplayName));
            var more = status.OutdatedClients.Count > 5 ? $" 等 {status.OutdatedClients.Count} 台" : string.Empty;

            await alerting.RaiseAsync(
                AgentVersionDriftAlertKey,
                AlertLevel.Warning,
                "agent_version_drift",
                $"{status.OutdatedClients.Count} 台客户端仍在旧版本",
                $"服务端随附版本 {status.BundledVersion} 从 {status.BundledAvailableSince:yyyy-MM-dd} 起可用，"
                + $"已超过 {status.DriftAlertDays} 天，以下机器仍低于它：{names}{more}。"
                + "版本落后本身不影响已经入库的备份，但只在新版本里修好的缺陷会在这些机器上继续复现。",
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent 版本漂移巡检异常");
        }
    }

    /// <summary>Agent 版本漂移告警键（聚合，全局一条）。</summary>
    private const string AgentVersionDriftAlertKey = "system:agent_version:drift";

    /// <summary>
    /// R9 / R10：服务端 TLS 证书与客户端 CA 的到期巡检。
    ///
    /// 挂在这个 worker 上而不是启动时跑一次：证书到期是一个随时间推进的状态，
    /// 只在启动时检查意味着一台连续跑一年的服务端永远不会再检查第二次——
    /// 而它恰恰是最可能跑到证书到期的那一台。
    /// </summary>
    private async Task RunCertificateLifecycleCheckAsync(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var checker = scope.ServiceProvider.GetRequiredService<ICertificateLifecycleChecker>();
            await checker.CheckAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "证书到期巡检异常");
        }
    }

    /// <summary>
    /// R4：配置备份包超期巡检。
    ///
    /// 导出是**手动触发**的（2026-09-10 定板：不做定时自动导出），
    /// 于是这条告警是唯一的兜底——没有任何定时任务替人记着这件事。
    /// 从未导出过同样算超期：那恰恰是最该被看见的状态。
    ///
    /// 公开是为了能单测。
    /// </summary>
    public async Task RunConfigBackupOverdueCheckAsync(IServiceScope scope, CancellationToken ct)
    {
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
        var configBackup = scope.ServiceProvider.GetRequiredService<IConfigBackupService>();

        ConfigBackupStatusDto status;
        try
        {
            status = await configBackup.GetStatusAsync(ct);
        }
        catch (BusinessException)
        {
            // 非一键部署形态下没有服务端数据目录，这件事不适用，也就没什么可报的。
            return;
        }

        if (!status.Overdue)
        {
            await alerting.RecoverAsync(ConfigBackupOverdueAlertKey, ct);
            return;
        }

        var message = status.LatestExportedAt is null
            ? $"从未导出过配置备份包。包内是服务端密钥、客户端 CA 与整库转储——"
              + $"没有它，仓库里的备份文件即使还在，也认不出哪个文件属于哪台机器、哪个任务、哪一天，"
              + $"同时所有客户端身份作废。请在「系统设置 → 配置备份」中导出一份并复制到另一台机器。"
            : $"最近一份配置备份包是 {status.LatestExportedAt:yyyy-MM-dd HH:mm} UTC 导出的，"
              + $"已经过去 {status.AgeDays} 天（阈值 {status.OverdueDays} 天）。导出是手动触发的，没有定时任务替你记着。";

        await alerting.RaiseAsync(
            ConfigBackupOverdueAlertKey,
            AlertLevel.Critical,
            "config_backup_overdue",
            "配置备份包超期未导出",
            message,
            ct: ct);
    }

    /// <summary>配置备份包超期告警键。</summary>
    private const string ConfigBackupOverdueAlertKey = "system:config_backup:overdue";

    /// <summary>
    /// A1：客户端存活判定。
    ///
    /// 公开是为了能单测：判松了离线永远报不出来，判紧了正在干活的机器天天被误报成离线，
    /// 而后者会让人很快学会无视这个告警——两边都要命。
    ///
    /// 刻意不用 ExecuteUpdateAsync 批量改：批量 UPDATE 拿不到「哪些客户端刚刚转成离线」
    /// 这个集合，就发不出告警——而告警才是这件事的全部意义。客户端数量级是几十到几百，
    /// 一次全量加载没有性能问题，idx_clients_status_heartbeat (status, last_heartbeat_at)
    /// 正好覆盖这个查询。
    /// </summary>
    public async Task RunClientLivenessAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();

        var suspectedAfter = Math.Max(1, await settings.GetIntAsync(SuspectedOfflineKey, 180, ct));
        var offlineAfter = Math.Max(suspectedAfter, await settings.GetIntAsync(OfflineThresholdKey, 300, ct));
        var now = DateTime.UtcNow;

        // 只看仍在服役的客户端：待审批 / 已禁用 / 已注销 / 证书过期都不参与判定，状态不被改写。
        var watched = await db.Clients
            .Where(c => c.Status == ClientStatus.Online || c.Status == ClientStatus.SuspectedOffline)
            .ToListAsync(ct);

        var offlineTransitions = new List<ClientEntity>();
        var suspectedCount = 0;

        foreach (var client in watched)
        {
            ct.ThrowIfCancellationRequested();

            // 心跳与「最近一次任何请求」取较晚者。只认心跳的那一版判的不是「它还在不在」，
            // 而是「它有没有按时汇报」——一台正在传 5 GB 备份、每几秒就领指令 / 报进度 /
            // 传分块的机器，心跳被大文件哈希拖住就会被判离线并发严重告警，而它正在好好干活。
            // 反过来也一样：真的没了的机器，这两个时间都会停住，判定不会变松。
            //
            // 刚审批完还没来得及说过一句话的，用 ApprovedAt 兜底（再兜 CreatedAt），
            // 不能一上来就判离线。
            var lastSeen = Later(client.LastHeartbeatAt, client.LastSeenAt)
                ?? client.ApprovedAt ?? client.CreatedAt;
            var silence = (now - lastSeen).TotalSeconds;

            if (silence >= offlineAfter)
            {
                if (client.Status != ClientStatus.Offline)
                {
                    client.Status = ClientStatus.Offline;
                    offlineTransitions.Add(client);   // 事务提交后再发告警
                }
            }
            else if (silence >= suspectedAfter && client.Status == ClientStatus.Online)
            {
                client.Status = ClientStatus.SuspectedOffline;
                suspectedCount++;
            }
        }

        if (offlineTransitions.Count == 0 && suspectedCount == 0)
            return;

        await db.SaveChangesAsync(ct);

        // 告警是旁路副作用，放在状态落库之后（与 AgentHeartbeatService 的做法一致）：
        // 告警去重冲突不该把客户端状态一起回滚。
        foreach (var client in offlineTransitions)
        {
            await alerting.RaiseAsync(
                $"client:{client.Id}:offline",
                AlertLevel.Critical,
                "client_offline",
                $"客户端 {client.DisplayName} 已离线",
                // 告警正文写「最近一次通信」而不是「最近心跳」：判定用的就是这个时间，
                // 两处写不同的数会让人拿着告警去对界面上的心跳时间，怎么对都对不上。
                Later(client.LastHeartbeatAt, client.LastSeenAt) is not { } seen
                    ? $"从未收到过这台机器的任何请求，已静默超过 {offlineAfter} 秒"
                    : $"最近一次通信 {seen:yyyy-MM-dd HH:mm:ss} UTC，已静默超过 {offlineAfter} 秒",
                clientId: client.Id,
                ct: ct);
        }

        _logger.LogInformation("客户端存活巡检完成：转离线 {Offline} 个，转疑似离线 {Suspected} 个",
            offlineTransitions.Count, suspectedCount);
    }

    /// <summary>
    /// A4：服务端仓库/暂存磁盘自检。
    ///
    /// UploadSessionService 在创建上传会话时检查暂存空间不足则抛 507，那是「拒收」不是「预警」——
    /// 第一次知道磁盘要满，是备份已经传不上来的时候。这里提前把它变成告警。
    /// </summary>
    /// <summary>两个时间取较晚的那个；都为空则为空。</summary>
    private static DateTime? Later(DateTime? left, DateTime? right) =>
        left is null ? right : right is null ? left : (left > right ? left : right);

    /// <summary>
    /// 公开是为了能单测：这一段原本有两条静默路径（解析失败 return、盘未就绪 continue），
    /// 结果是「磁盘快满了」会报警，「磁盘整个不见了」反而一声不响。这种反向的沉默必须钉住。
    /// </summary>
    public async Task RunStorageSelfCheckAsync(IServiceScope scope, CancellationToken ct)
    {
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
        var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();

        var warnPercent = Math.Clamp(await settings.GetIntAsync(StorageAlertPercentKey, 15, ct), 1, 99);

        // 两个根分别解析、分别报警。原先合在一个 try 里，仓库路径没了会把暂存区的检查
        // 一起带走——恰恰是「盘没了」这种最该说话的时候，整轮自检全哑。
        var roots = new List<(string Name, string? Root)>
        {
            ("仓库", await ResolveStorageRootAsync("仓库", () => storage.GetRepositoryRootAsync(ct), alerting, ct)),
            ("暂存区", await ResolveStorageRootAsync("暂存区", () => storage.GetStagingRootAsync(ct), alerting, ct))
        };

        foreach (var (name, root) in roots)
        {
            ct.ThrowIfCancellationRequested();

            if (root is null)
                continue;   // 解析阶段已经报过 server_storage_unavailable

            var alertKey = $"system:storage:{name}";
            try
            {
                var pathRoot = Path.GetPathRoot(Path.GetFullPath(root));
                if (string.IsNullOrWhiteSpace(pathRoot))
                {
                    await RaiseStorageUnavailableAsync(alerting, name,
                        $"无法从路径 {root} 判定所在卷", ct);
                    continue;
                }

                var drive = new DriveInfo(pathRoot);
                if (!drive.IsReady)
                {
                    // 外挂盘掉电、网络盘断开、卷未挂载都落在这里。原来是 continue：
                    // 盘整个不见了，系统一个字都不说。
                    await RaiseStorageUnavailableAsync(alerting, name,
                        $"{pathRoot} 未就绪（外挂盘掉线 / 网络盘断开 / 卷未挂载）", ct);
                    continue;
                }

                if (drive.TotalSize <= 0)
                {
                    await RaiseStorageUnavailableAsync(alerting, name,
                        $"{drive.Name} 报告的总容量为 0，无法判定剩余空间", ct);
                    continue;
                }

                await alerting.RecoverAsync(StorageUnavailableAlertKey(name), ct);

                var freePercent = drive.AvailableFreeSpace * 100.0 / drive.TotalSize;
                if (freePercent <= warnPercent)
                {
                    await alerting.RaiseAsync(
                        alertKey,
                        freePercent <= StorageCriticalPercent ? AlertLevel.Critical : AlertLevel.Warning,
                        "server_storage_low",
                        $"服务端{name}磁盘空间不足",
                        $"{drive.Name} 可用 {freePercent:0.#}%（阈值 {warnPercent}%），备份上传即将开始被拒绝",
                        ct: ct);
                }
                else
                {
                    await alerting.RecoverAsync(alertKey, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "服务端{Name}磁盘容量读取失败：{Root}", name, root);
                await RaiseStorageUnavailableAsync(alerting, name, $"读取 {root} 的卷信息失败：{ex.Message}", ct);
            }
        }
    }

    /// <summary>
    /// 解析一个存储根；失败时报 Critical 并返回 null（本轮跳过这个根的容量检查）。
    /// 解析内部会对配置路径执行 CreateDirectory，路径被删、盘符不存在、NAS 掉线、
    /// 权限丢了都会在这里抛出来——每一种都是「备份现在就落不了地」，不是可以忽略的小事。
    /// </summary>
    private async Task<string?> ResolveStorageRootAsync(
        string name,
        Func<Task<string>> resolve,
        IAlertingService alerting,
        CancellationToken ct)
    {
        try
        {
            return await resolve();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "服务端{Name}存储根目录解析失败", name);
            await RaiseStorageUnavailableAsync(alerting, name, $"存储根目录不可用：{ex.Message}", ct);
            return null;
        }
    }

    /// <summary>
    /// 「盘没了」的告警键刻意与 server_storage_low 的 <c>system:storage:{name}</c> 分开。
    /// 共用一个键会让两件事互相覆盖：盘掉线时把「快满了」顶掉，盘回来时又把「没了」顶掉，
    /// 而这两件事的处置完全不同（一个是加盘，一个是插线）。
    /// </summary>
    private static string StorageUnavailableAlertKey(string name) => $"system:storage:{name}:unavailable";

    private static Task RaiseStorageUnavailableAsync(
        IAlertingService alerting,
        string name,
        string detail,
        CancellationToken ct) =>
        alerting.RaiseAsync(
            StorageUnavailableAlertKey(name),
            AlertLevel.Critical,
            "server_storage_unavailable",
            $"服务端{name}存储不可达",
            detail,
            ct: ct);
}
