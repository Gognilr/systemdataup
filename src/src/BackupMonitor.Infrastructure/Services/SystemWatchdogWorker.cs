using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
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
    }

    /// <summary>
    /// A1：客户端存活判定。
    ///
    /// 刻意不用 ExecuteUpdateAsync 批量改：批量 UPDATE 拿不到「哪些客户端刚刚转成离线」
    /// 这个集合，就发不出告警——而告警才是这件事的全部意义。客户端数量级是几十到几百，
    /// 一次全量加载没有性能问题，idx_clients_status_heartbeat (status, last_heartbeat_at)
    /// 正好覆盖这个查询。
    /// </summary>
    private async Task RunClientLivenessAsync(IServiceScope scope, CancellationToken ct)
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

            // 刚审批完还没来得及发第一次心跳的，用 ApprovedAt 兜底（再兜 CreatedAt），
            // 不能一上来就判离线。
            var lastSeen = client.LastHeartbeatAt ?? client.ApprovedAt ?? client.CreatedAt;
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
                client.LastHeartbeatAt is null
                    ? $"从未收到心跳，已静默超过 {offlineAfter} 秒"
                    : $"最近心跳 {client.LastHeartbeatAt:yyyy-MM-dd HH:mm:ss} UTC，已静默超过 {offlineAfter} 秒",
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
    private async Task RunStorageSelfCheckAsync(IServiceScope scope, CancellationToken ct)
    {
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
        var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();

        var warnPercent = Math.Clamp(await settings.GetIntAsync(StorageAlertPercentKey, 15, ct), 1, 99);

        string repositoryRoot;
        string stagingRoot;
        try
        {
            // 根目录解析会对配置路径执行 CreateDirectory，路径不存在或没权限时抛异常。
            // 巡检不该因此整轮失败，取不到就跳过本轮磁盘自检。
            repositoryRoot = await storage.GetRepositoryRootAsync(ct);
            stagingRoot = await storage.GetStagingRootAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "服务端存储根目录解析失败，本轮跳过磁盘自检");
            return;
        }

        foreach (var (name, root) in new[] { ("仓库", repositoryRoot), ("暂存区", stagingRoot) })
        {
            ct.ThrowIfCancellationRequested();

            var alertKey = $"system:storage:{name}";
            try
            {
                var pathRoot = Path.GetPathRoot(Path.GetFullPath(root));
                if (string.IsNullOrWhiteSpace(pathRoot))
                    continue;

                var drive = new DriveInfo(pathRoot);
                if (!drive.IsReady || drive.TotalSize <= 0)
                    continue;

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
            }
        }
    }
}
