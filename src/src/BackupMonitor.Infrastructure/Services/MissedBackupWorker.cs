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
        //
        // 客户端被禁用 / 注销 / 还没审批时同理：这个任务本来就不该产出备份，
        // 队列那边（ExecutionQueueService.SkipReason）压根不会给它下发指令。
        // 继续判它「漏备份」会得到一条永远不会恢复的严重告警，
        // 而「先把这台机器停掉再说」正是排障时最自然的动作——一停就炸一屏告警。
        // 判据与 SkipReason 保持同一口径，两边分家就会出现「队列不发、巡检却在报」。
        var tasks = await db.BackupTasks
            .Include(t => t.Client)
            .Where(t => t.Enabled
                && t.ScanSchedule != null
                && t.TaskMode != TaskMode.Paused
                && t.TaskMode != TaskMode.MonitorOnly
                && t.Client.Status != ClientStatus.Disabled
                && t.Client.Status != ClientStatus.Revoked
                && t.Client.Status != ClientStatus.PendingApproval)
            .ToListAsync(ct);

        // 挂进启用中计划的任务由下面的「按计划判定」分支负责，这里要排除掉。
        // 两边都判的话，同一个任务会按两套完全不同的时刻各判一次
        // （任务自己的 cron 已经不再下发给 Agent，按它算出来的 prevDue 没有意义）。
        var planManagedTaskIds = await db.BackupPlanItems
            .Where(i => i.Plan.Enabled)
            .Select(i => i.TaskId)
            .ToListAsync(ct);
        var planManaged = planManagedTaskIds.ToHashSet();

        // 审计 G-13：所有任务的 ID，包括上面那条 Where 排除掉的（停用 / 暂停 / 只监控）。
        // 一个任务先报了 missed，随后被停用或改成只监控，那条严重告警就再也没人恢复——
        // 而「先把这个任务停掉再说」恰恰是排障时最自然的动作，正好触发它。
        var allTaskIds = await db.BackupTasks.Select(t => t.Id).ToListAsync(ct);

        // 只有真正走到「比对 LastSuccessAt 与 prevDue」那一步的才算判定过。
        // 下面四处 continue 一律不算——它们是「这轮没法判定」，不是「判定为正常」。
        var judgedTaskIds = new HashSet<Guid>();

        var raised = 0;
        var recovered = 0;

        // 判定与告警对按 cron 和按计划两条路径完全一致，只有「这个时刻是谁定的」那句说明不同。
        // alert_key 也刻意共用：一个任务只可能属于其中一条路径，共用键保证换路径时旧告警会被恢复，
        // 不会在告警中心留下两条同义的漏备份。
        async Task JudgeAsync(Core.Entities.Backup.BackupTask task, DateTime prevDue, TimeZoneInfo tz, string dueSource)
        {
            judgedTaskIds.Add(task.Id);

            var alertKey = $"task:{task.Id}:missed";
            if (task.LastSuccessAt is null || task.LastSuccessAt < prevDue)
            {
                var localDue = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(prevDue, DateTimeKind.Utc), tz);

                await alerting.RaiseAsync(
                    alertKey,
                    task.ImportanceLevel >= ImportanceLevel.High ? AlertLevel.Critical : AlertLevel.Warning,
                    "backup_missed",
                    $"任务 {task.Name} 未按计划产生备份",
                    $"{dueSource}应该在 {localDue:yyyy-MM-dd HH:mm}（{tz.Id}）备份，已经多等了 {graceMinutes} 分钟，仍然没有备份成功",
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

        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();

            if (planManaged.Contains(task.Id))
                continue;

            // 坑一：新部署或长期停机后重启，所有任务都会命中「上一次计划早已过去」。
            // 任务创建至今不足一个宽限期时直接跳过，避免首轮炸一屏告警。
            if (now - task.CreatedAt < grace)
                continue;

            if (!CronExpression.TryParse(task.ScanSchedule, out var cron, out _) || cron is null)
                continue;

            // 解析不了就退回 UTC，但必须留下痕迹：凌晨 2:00 的计划会因此按
            // 北京时间上午 10:00 判定，而那正是「有没有漏备份」的基准。
            if (!PlanSchedule.TryResolveTimeZone(task.ScheduleTimezone, out var tz))
                _logger.LogWarning("任务 {Task} 的时区 {Zone} 在本机解析不了，本轮按 UTC 判定",
                    task.Name, task.ScheduleTimezone);

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

            await JudgeAsync(task, prevDue.Value, tz, "按计划");
        }

        // 按备份计划判定。计划驱动的任务的 scan_schedule 在下发时被抹成 null
        // （由服务端计划触发，不再由 Agent 的 cron 触发），因此上面那条按 cron 的分支
        // 一个都看不见——挂进计划等于退出漏备份巡检，而计划本身没触发、卡住、
        // 或者机器整晚关机，都不会有任何一条告警。
        var plans = await db.BackupPlans
            .Include(p => p.Items).ThenInclude(i => i.Task).ThenInclude(t => t.Client)
            .Where(p => p.Enabled)
            .ToListAsync(ct);

        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();

            var tz = PlanSchedule.ResolveTimeZone(plan.Timezone);
            var prevDue = PlanSchedule.GetPreviousOccurrence(
                plan.ScheduleKind == PlanScheduleKind.Weekly,
                plan.RunAt,
                PlanSchedule.ParseDays(plan.DaysOfWeek),
                tz,
                now);

            if (prevDue is null)
                continue;

            if (now < prevDue.Value + grace)
                continue;   // 还在宽限期内

            foreach (var item in plan.Items)
            {
                var task = item.Task;

                // 与按 cron 的分支同一个理由：刚建出来的任务不算漏。
                if (now - task.CreatedAt < grace)
                    continue;

                // 队列本来就不会给这些任务下发指令，判它们「漏备份」得到的是一条永不恢复的告警。
                if (ExecutionQueueService.SkipReason(task) is not null)
                    continue;

                await JudgeAsync(task, prevDue.Value, tz, $"计划「{plan.Name}」");
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

}
