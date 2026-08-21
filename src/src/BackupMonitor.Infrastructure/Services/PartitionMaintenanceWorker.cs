using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>每日提前创建遥测分区并按保留期轮转心跳分区。</summary>
public sealed class PartitionMaintenanceWorker : BackgroundService
{
    public const string IntervalKey = "partition_maintenance_interval_hours";
    public const string LockKey = "worker:partition_maintenance";

    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PartitionMaintenanceWorker> _logger;

    public PartitionMaintenanceWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PartitionMaintenanceWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("遥测分区维护工作器已启动");
        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalHours = 24;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                intervalHours = Math.Max(1, await settings.GetIntAsync(IntervalKey, 24, stoppingToken));
                var locks = scope.ServiceProvider.GetRequiredService<IScheduledLockService>();
                if (await locks.TryAcquireAsync(LockKey, LockTtl, stoppingToken))
                {
                    try
                    {
                        var db = scope.ServiceProvider.GetRequiredService<BackupMonitor.Infrastructure.Data.AppDbContext>();
                        var maintenance = scope.ServiceProvider.GetRequiredService<PartitionMaintenanceService>();
                        var now = DateTime.UtcNow;
                        var created = await maintenance.EnsureFuturePartitionsAsync(db, now, stoppingToken);
                        var retentionDays = Math.Max(
                            1,
                            await settings.GetIntAsync("telemetry_heartbeat_retention_days", 30, stoppingToken));
                        var dropped = await maintenance.DropExpiredHeartbeatPartitionsAsync(
                            db,
                            now,
                            retentionDays,
                            stoppingToken);
                        if (created.Count > 0 || dropped.Count > 0)
                            _logger.LogInformation("遥测分区维护完成：创建 {Created}，删除过期心跳分区 {Dropped}", created.Count, dropped.Count);
                    }
                    finally
                    {
                        await locks.ReleaseAsync(LockKey, CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "遥测分区维护轮询异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
