using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 漏备份巡检工作器（整改 A2）。
///
/// 「今天该备份的没备份」原先唯一的体现是概览页日历上的一个红格子——需要有人主动打开
/// 那一页并看懂那个格子；不进告警中心、不发邮件、不进待办。这个 worker 把它变成一条
/// 正经告警：按任务的 cron 算出「上一次本该跑的时刻」，超过宽限期仍无成功入库就报警。
///
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁），幂等
/// （告警按 alert_key 聚合，重复轮次不会刷屏）。
/// </summary>
public class MissedBackupWorker : BackgroundService
{
    /// <summary>执行间隔（分钟）system_settings 键</summary>
    public const string IntervalKey = "missed_backup_scan_interval_minutes";

    /// <summary>宽限期（分钟）system_settings 键：计划时刻起多久没有成功入库才算漏备份</summary>
    public const string GraceKey = "missed_backup_grace_minutes";

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:missed_backup";

    /// <summary>锁 TTL：须大于单轮最坏耗时</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MissedBackupWorker> _logger;

    public MissedBackupWorker(IServiceScopeFactory scopeFactory, ILogger<MissedBackupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("漏备份巡检工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = 15;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                intervalMinutes = Math.Max(1, await settings.GetIntAsync(IntervalKey, 15, stoppingToken));

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
                _logger.LogError(ex, "漏备份巡检轮询异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 单轮漏备份巡检。公开而不是私有，是为了让集成测试能确定性地驱动一轮——
    /// 靠 StartAsync 等首轮跑完再轮询断言，测试会变成对时序的赌博。
    /// </summary>
    public async Task RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();

        var graceMinutes = Math.Max(1, await settings.GetIntAsync(GraceKey, 120, ct));
        var grace = TimeSpan.FromMinutes(graceMinutes);
        var now = DateTime.UtcNow;

        // paused / monitor_only 的任务不承诺产出备份，不参与漏备份判定。
        var tasks = await db.BackupTasks
            .Where(t => t.Enabled
                && t.ScanSchedule != null
                && t.TaskMode != TaskMode.Paused
                && t.TaskMode != TaskMode.MonitorOnly)
            .ToListAsync(ct);

        // 审计 G-13：所有任务的 ID，包括上面那条 Where 排除掉的（停用 / 暂停 / 只监控）。
        // 一个任务先报了 missed，随后被停用或改成只监控，那条严重告警就再也没人恢复——
        // 而「先把这个任务停掉再说」恰恰是排障时最自然的动作，正好触发它。
        var allTaskIds = await db.BackupTasks.Select(t => t.Id).ToListAsync(ct);

        // 只有真正走到「比对 LastSuccessAt 与 prevDue」那一步的才算判定过。
        // 下面四处 continue 一律不算——它们是「这轮没法判定」，不是「判定为正常」。
        var judgedTaskIds = new HashSet<Guid>();

        var raised = 0;
        var recovered = 0;

        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();

            // 坑一：新部署或长期停机后重启，所有任务都会命中「上一次计划早已过去」。
            // 任务创建至今不足一个宽限期时直接跳过，避免首轮炸一屏告警。
            if (now - task.CreatedAt < grace)
                continue;

            if (!CronExpression.TryParse(task.ScanSchedule, out var cron, out _) || cron is null)
                continue;

            var tz = ResolveTimeZone(task.ScheduleTimezone);

            var prevDue = cron.GetPreviousOccurrence(now, tz);
            if (prevDue is null)
                continue;

            // 坑二：`*/5 * * * *` 这类分钟级 cron「漏一次」没有运维意义，
            // 而且宽限期内会累计出几十次「漏」。相邻两次计划间隔小于宽限期的任务直接跳过。
            var beforePrevDue = cron.GetPreviousOccurrence(prevDue.Value, tz);
            if (beforePrevDue is not null && prevDue.Value - beforePrevDue.Value < grace)
                continue;

            var deadline = prevDue.Value + grace;
            if (now < deadline)
                continue;   // 还在宽限期内

            judgedTaskIds.Add(task.Id);

            var alertKey = $"task:{task.Id}:missed";
            if (task.LastSuccessAt is null || task.LastSuccessAt < prevDue)
            {
                var localDue = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(prevDue.Value, DateTimeKind.Utc), tz);

                await alerting.RaiseAsync(
                    alertKey,
                    task.ImportanceLevel >= ImportanceLevel.High ? AlertLevel.Critical : AlertLevel.Warning,
                    "backup_missed",
                    $"任务 {task.Name} 未按计划产生备份",
                    $"按计划应该在 {localDue:yyyy-MM-dd HH:mm}（{tz.Id}）备份，已经多等了 {graceMinutes} 分钟，仍然没有备份成功",
                    clientId: task.ClientId,
                    taskId: task.Id,
                    ct: ct);
                raised++;
            }
            else
            {
                await alerting.RecoverAsync(alertKey, ct);
                recovered++;
            }
        }

        // 审计 G-13：本轮没被判定过的任务，统一恢复它们的漏备份告警。
        // RecoverAsync 内部有「没有活动告警就直接返回」的前置查询，
        // 因此对绝大多数任务这只是一次索引扫描，不产生 UPDATE。
        foreach (var taskId in allTaskIds.Where(id => !judgedTaskIds.Contains(id)))
        {
            ct.ThrowIfCancellationRequested();
            await alerting.RecoverAsync($"task:{taskId}:missed", ct);
        }

        if (raised > 0)
            _logger.LogInformation("漏备份巡检完成：触发 {Raised} 条，恢复 {Recovered} 条", raised, recovered);
    }

    /// <summary>
    /// 解析任务时区。服务端存的是 IANA ID（默认 Asia/Shanghai），部分 Windows 主机只认
    /// Windows 时区 ID，这里保持与 ReportService.ResolveTimeZone 相同的回退链——
    /// 两处判的是同一个「任务的计划时刻」，算出不同的答案没有意义。
    /// 最终仍解析不了就退回 UTC：服务端不能按本机时区猜，但也不能因此不做判定。
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            var windowsId = id.Trim() switch
            {
                "Asia/Tokyo" => "Tokyo Standard Time",
                "Asia/Shanghai" => "China Standard Time",
                "Asia/Singapore" => "Singapore Standard Time",
                "Europe/London" => "GMT Standard Time",
                "America/New_York" => "Eastern Standard Time",
                "America/Los_Angeles" => "Pacific Standard Time",
                _ => null
            };

            if (windowsId is not null)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
                catch (TimeZoneNotFoundException) { }
            }

            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
