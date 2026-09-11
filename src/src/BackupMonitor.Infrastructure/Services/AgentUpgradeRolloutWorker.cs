using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 客户端升级的分批推进（整改清单 2026-09-10 · R20）。
///
/// 它守着这次整改最核心的一条改动：**成功不再由指令回报决定**。
/// 一台机器要等到心跳回来、并且自报的版本号确实变成目标版本才算成功；
/// 等不到就是失败，而失败会把后面的批次全部刹住——
/// 一个坏包同时推给 29 台就是 29 次上门，升级失败的机器连不上服务端，
/// 连修复指令都收不到。
///
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁）。
/// </summary>
public class AgentUpgradeRolloutWorker : BackgroundService
{
    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:agent_upgrade_rollout";

    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 30 秒一轮。它等的是「客户端心跳回来没有」，而心跳默认 60 秒一次——
    /// 比心跳密一档就够了，再密只是空跑查询；再稀则会让每一批的放行都慢半分钟以上，
    /// 而金丝雀分批本来就要串行等好几批。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentUpgradeRolloutWorker> _logger;

    public AgentUpgradeRolloutWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AgentUpgradeRolloutWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("客户端升级分批推进工作器已启动");

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
                _logger.LogError(ex, "客户端升级分批推进轮询异常");
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

    /// <summary>公开是为了能单测：分批刹车只在真出坏包那天起作用，那一天没有第二次机会。</summary>
    public async Task RunOnceAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAgentUpgradeService>();

        var running = await db.AgentUpgrades.AsNoTracking()
            .Where(u => u.Status == AgentUpgradeStatus.Pending || u.Status == AgentUpgradeStatus.Running)
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var id in running)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await service.AdvanceAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 一次下发出问题不该把别的下发也卡住：它们各自独立，
                // 而卡住的那一个会留在 running 状态，下一轮还会再试。
                _logger.LogError(ex, "推进升级下发 {UpgradeId} 失败", id);
            }
        }
    }
}
