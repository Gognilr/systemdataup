using BackupMonitor.Core.Entities.Execution;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 顺序执行器（V028）：备份计划到点执行与批量上传的并发闸，共用这一个。
///
/// 做三件事：
///   一、到点的备份计划产生一次执行（execution_runs + 每项 execution_run_items）；
///   二、判定在跑的项是否终结（指令失败 / 上传会话终结 / 超时）；
///   三、按 max_concurrent 放行还没开跑的项——并发度 1 就是严格一个接一个。
///
/// 「终结」的判定刻意包含超时：一台关机的客户端不能让整晚的计划全部不执行
/// （方案 A 的第二个决定）。超时的项判失败、发告警、继续下一项。
///
/// 单实例（scheduled_locks 数据库锁），幂等（重复执行不产生额外状态变化：
/// 指令下发带幂等键，终结判定只从 pending/running 往终态走）。
/// </summary>
public class SequentialExecutionWorker : BackgroundService
{
    /// <summary>推进间隔（秒）system_settings 键</summary>
    public const string PollKey = "execution_queue_poll_seconds";

    /// <summary>预检成功后等多久还没有上传会话，就判定为「这次没有新备份要传」（分钟）</summary>
    public const string NoUploadGraceKey = "execution_queue_no_upload_grace_minutes";

    /// <summary>补跑上限（小时）：服务端停机超过这个时长，过期的那次计划不再补跑</summary>
    public const string CatchupKey = "execution_queue_catchup_hours";

    /// <summary>
    /// 全局活动上传会话上限（D3）。它覆盖所有来源——计划执行、批量操作、手动立即备份、
    /// 自动模式下预检通过后的自动上传——而不只是手动点击那一条路。
    ///
    /// 闸放在下发侧（这里决定什么时候把指令发出去），不放在 UploadSessionService 的
    /// 建会话侧：Agent 对 UPLOAD_SESSION_CONFLICT 没有退避重试，在那一步 409
    /// 等于把限流变成备份失败。这不是优化建议，是必须遵守的约束。
    /// UploadSessionService 里那条按客户端的检查是兜底、不是队列，保持不动。
    /// </summary>
    public const string GlobalUploadLimitKey = "max_concurrent_uploads_total";

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:execution_queue";

    /// <summary>锁 TTL：须大于单轮最坏耗时</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);

    /// <summary>指令层面已经不可能再有结果的状态</summary>
    private static readonly CommandStatus[] DeadCommandStatuses =
        [CommandStatus.Failed, CommandStatus.Cancelled, CommandStatus.Expired, CommandStatus.Rejected];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SequentialExecutionWorker> _logger;

    public SequentialExecutionWorker(IServiceScopeFactory scopeFactory, ILogger<SequentialExecutionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("顺序执行器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var pollSeconds = 15;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                pollSeconds = Math.Clamp(await settings.GetIntAsync(PollKey, 15, stoppingToken), 5, 300);

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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "顺序执行器轮询异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>一轮推进：先让到点的计划产生执行，再推进所有未结束的执行</summary>
    public async Task RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueueService>();
        var commands = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();

        var now = DateTime.UtcNow;

        await PromoteDuePlansAsync(db, queue, settings, now, ct);
        await AdvanceRunsAsync(db, commands, alerting, settings, now, ct);
    }

    // ---------- 一、到点的计划 ----------

    /// <summary>
    /// 到点的计划产生一次执行。判据是「上一次应执行时刻」还没跑过：
    /// last_run_at 早于它，且这一时刻还没有对应的 run（唯一索引 uq_execution_runs_plan_scheduled 兜底）。
    /// 服务端停机太久时不补跑——凌晨两点的计划在第二天下午补一次没有意义，只会撞上白天的业务。
    /// </summary>
    private async Task PromoteDuePlansAsync(
        AppDbContext db, IExecutionQueueService queue, SystemSettingsProvider settings, DateTime now, CancellationToken ct)
    {
        var catchupHours = Math.Clamp(await settings.GetIntAsync(CatchupKey, 12, ct), 1, 168);
        var plans = await db.BackupPlans.AsNoTracking().Where(p => p.Enabled).ToListAsync(ct);

        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();

            var tz = PlanSchedule.ResolveTimeZone(plan.Timezone);
            var due = PlanSchedule.GetPreviousOccurrence(
                plan.ScheduleKind == PlanScheduleKind.Weekly,
                plan.RunAt,
                PlanSchedule.ParseDays(plan.DaysOfWeek),
                tz,
                now);

            if (due is null)
                continue;
            if (plan.LastRunAt is not null && plan.LastRunAt >= due)
                continue;
            if (now - due.Value > TimeSpan.FromHours(catchupHours))
            {
                _logger.LogWarning("备份计划 {Plan} 的 {Due:yyyy-MM-dd HH:mm} 那次已超过 {Hours} 小时，不再补跑",
                    plan.Name, due.Value, catchupHours);
                // 标记为已处理，否则每一轮都会重新算一次并重复打这条日志
                await db.BackupPlans.Where(p => p.Id == plan.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastRunAt, due.Value), ct);
                continue;
            }

            try
            {
                await queue.CreatePlanRunAsync(plan.Id, due, "schedule", null, ct);
            }
            catch (DbUpdateException ex)
            {
                // 唯一索引撞车：另一实例刚刚为同一时刻建过。正常竞态，不是错误。
                _logger.LogDebug(ex, "备份计划 {Plan} 的 {Due} 那次已由其他实例创建", plan.Name, due);
            }
        }
    }

    // ---------- 二、推进未结束的执行 ----------

    private async Task AdvanceRunsAsync(
        AppDbContext db,
        ICommandDispatcher commands,
        IAlertingService alerting,
        SystemSettingsProvider settings,
        DateTime now,
        CancellationToken ct)
    {
        var graceMinutes = Math.Clamp(await settings.GetIntAsync(NoUploadGraceKey, 10, ct), 1, 1440);
        var grace = TimeSpan.FromMinutes(graceMinutes);
        var globalUploadLimit = Math.Clamp(await settings.GetIntAsync(GlobalUploadLimitKey, 4, ct), 1, 64);

        var runs = await db.ExecutionRuns
            .Include(r => r.Items)
            .Where(r => r.Status == BatchStatus.Pending || r.Status == BatchStatus.Running
                || (r.Status == BatchStatus.Cancelled && r.FinishedAt == null))
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var item in run.Items.Where(i => i.Status == ExecutionItemStatus.Running).ToList())
                await EvaluateRunningItemAsync(db, alerting, run, item, grace, now, ct);

            // 取消掉的执行不再放行新项，只等在跑的那几项跑完
            if (run.Status != BatchStatus.Cancelled)
                await StartPendingItemsAsync(db, commands, run, globalUploadLimit, now, ct);

            await FinalizeRunAsync(db, run, now, ct);
        }
    }

    /// <summary>
    /// 判定一项是否终结。顺序：先超时（等不到它了），再看指令，最后看上传会话。
    /// </summary>
    private async Task EvaluateRunningItemAsync(
        AppDbContext db,
        IAlertingService alerting,
        ExecutionRun run,
        ExecutionRunItem item,
        TimeSpan noUploadGrace,
        DateTime now,
        CancellationToken ct)
    {
        var startedAt = item.StartedAt ?? run.StartedAt ?? run.CreatedAt;
        if (now - startedAt >= TimeSpan.FromMinutes(run.ItemTimeoutMinutes))
        {
            await FinishItemAsync(db, item, ExecutionItemStatus.Timeout,
                $"超过 {run.ItemTimeoutMinutes} 分钟仍未完成，已判定超时并继续下一项", now, ct);

            var task = await db.BackupTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == item.TaskId, ct);
            await alerting.RaiseAsync(
                $"execution_item:{item.Id}:timeout",
                AlertLevel.Warning,
                "execution_item_timeout",
                $"备份计划里的这一项等超时了（{task?.Name ?? item.TaskId.ToString()}）",
                $"「{run.Name}」里的这一项等了 {run.ItemTimeoutMinutes} 分钟仍然没有结果，" +
                "多半是那台客户端关机或离线了。这一项按失败处理，计划继续执行下一项。",
                clientId: item.ClientId,
                taskId: item.TaskId,
                ct: ct);
            return;
        }

        var command = item.CommandId is null
            ? null
            : await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == item.CommandId, ct);

        if (command is not null && DeadCommandStatuses.Contains(command.Status))
        {
            await FinishItemAsync(db, item, ExecutionItemStatus.Failed,
                $"指令{EnumMapping.ToSnakeCase(command.Status)}：{command.ResultMessage ?? "无返回信息"}", now, ct);
            return;
        }

        // 指令还没执行完就继续等
        if (command is null || command.Status != CommandStatus.Succeeded)
            return;

        var session = await FindSessionAsync(db, item, startedAt, ct);
        if (session is null)
        {
            // 预检通过却一直没有上传会话，最常见的原因就是「这次压根没有新备份」。
            // 等一个宽限期再下这个结论，避免抢在服务端下发上传指令之前。
            var since = command.CompletedAt ?? startedAt;
            if (now - since >= noUploadGrace)
                await FinishItemAsync(db, item, ExecutionItemStatus.Succeeded, "没有需要上传的新备份", now, ct);
            return;
        }

        item.UploadSessionId = session.Id;
        switch (session.Status)
        {
            case UploadStatus.Committed:
                await FinishItemAsync(db, item, ExecutionItemStatus.Succeeded, "备份已入库", now, ct);
                break;
            case UploadStatus.Failed:
            case UploadStatus.Cancelled:
            case UploadStatus.Expired:
                await FinishItemAsync(db, item, ExecutionItemStatus.Failed,
                    $"上传{EnumMapping.ToSnakeCase(session.Status)}", now, ct);
                break;
            default:
                await db.SaveChangesAsync(ct);   // 只是把会话 ID 记下来，界面上能点过去
                break;
        }
    }

    /// <summary>
    /// 这一项对应的上传会话。批量上传按候选定位；计划驱动的任务只能按「这一项开跑之后
    /// 该任务新建的会话」定位——预检产生的候选 ID 在下发预检时还不存在。
    /// </summary>
    private static async Task<Core.Entities.Upload.UploadSession?> FindSessionAsync(
        AppDbContext db, ExecutionRunItem item, DateTime startedAt, CancellationToken ct)
    {
        var sessions = db.UploadSessions.AsNoTracking();

        if (item.CandidateBackupSetId is not null)
            return await sessions
                .Where(s => s.CandidateBackupSetId == item.CandidateBackupSetId)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);

        return await sessions
            .Where(s => s.TaskId == item.TaskId && s.CreatedAt >= startedAt)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task FinishItemAsync(
        AppDbContext db, ExecutionRunItem item, ExecutionItemStatus status, string message, DateTime now, CancellationToken ct)
    {
        item.Status = status;
        item.Message = message.Length > 1000 ? message[..1000] : message;
        item.FinishedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 两道并发闸：
    ///   一、这次执行自己的 max_concurrent（running 少于它才按 sort_order 放行下一项）——
    ///       这就是 upload_batches.max_concurrent_clients 一直缺的那段调度逻辑；
    ///   二、全局活动上传会话数（max_concurrent_uploads_total），跨执行、跨来源统一算。
    ///
    /// 第二道每放行一项就重查一次，而不是在循环外算一次：这一轮里刚放行的项马上就会建会话，
    /// 拿循环开始时的计数往下放，一轮就能超发好几个。查一次是一条 COUNT，
    /// 相比一次上传的代价可以忽略。
    ///
    /// 达到上限就 break 而不是判失败——排队是这一条的全部意义。名额由会话终结释放
    /// （committed / failed / cancelled / expired，僵尸会话由 LifecycleExpiryWorker 回收）。
    /// </summary>
    private async Task StartPendingItemsAsync(
        AppDbContext db, ICommandDispatcher commands, ExecutionRun run, int globalUploadLimit, DateTime now, CancellationToken ct)
    {
        var running = run.Items.Count(i => i.Status == ExecutionItemStatus.Running);
        var pending = run.Items
            .Where(i => i.Status == ExecutionItemStatus.Pending)
            .OrderBy(i => i.SortOrder)
            .ToList();

        // 批次上的临时限速要跟着上传指令一起下发（Agent 优先取 payload 里的 bandwidthLimitKbps）
        var batchBandwidthKbps = run.UploadBatchId is null
            ? null
            : await db.UploadBatches.AsNoTracking()
                .Where(b => b.Id == run.UploadBatchId)
                .Select(b => b.BandwidthLimitKbps)
                .FirstOrDefaultAsync(ct);

        foreach (var item in pending)
        {
            if (running >= run.MaxConcurrent)
                break;

            ct.ThrowIfCancellationRequested();

            var activeUploads = await CountActiveUploadsAsync(db, ct);
            if (activeUploads >= globalUploadLimit)
            {
                _logger.LogInformation(
                    "全局上传并发已达上限（{Active}/{Limit}），执行 {RunId} 的剩余项继续排队",
                    activeUploads, globalUploadLimit, run.Id);
                break;
            }

            try
            {
                object? payload = null;
                if (item.CandidateBackupSetId is not null)
                {
                    var task = await db.BackupTasks.AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Id == item.TaskId, ct);
                    payload = new
                    {
                        candidateBackupSetId = item.CandidateBackupSetId,
                        // batchId 保持下发：批次计数仍由 CommandService 在指令完结时累加
                        batchId = run.UploadBatchId,
                        bandwidthLimitKbps = batchBandwidthKbps ?? task?.BandwidthLimitKbps,
                        chunkSizeBytes = task?.ChunkSizeBytes
                    };
                }

                var command = await commands.CreateCommandAsync(
                    item.ClientId,
                    item.CommandType,
                    taskId: item.TaskId,
                    candidateBackupSetId: item.CandidateBackupSetId,
                    payload: payload,
                    idempotencyKey: $"queue:{item.Id}",
                    createdBy: run.TriggeredBy,
                    ct: ct);

                item.CommandId = command.Id;
                item.Status = ExecutionItemStatus.Running;
                item.StartedAt = now;
                item.Message = null;
                running++;

                if (run.Status == BatchStatus.Pending)
                {
                    run.Status = BatchStatus.Running;
                    run.StartedAt ??= now;
                }

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // 下发不出去（客户端被删、签名失败……）就是这一项失败，不能卡住后面的
                _logger.LogError(ex, "队列项 {ItemId} 下发指令失败", item.Id);
                await FinishItemAsync(db, item, ExecutionItemStatus.Failed, $"指令下发失败：{ex.Message}", now, ct);
            }
        }
    }

    /// <summary>
    /// 全服务端当前活动的上传会话数。刻意不按客户端分组——「一台客户端最多同时传几个」
    /// 由 UploadSessionService 管，这里管的是「服务端暂存盘同时被几路读写」，
    /// 十个任务在十台不同客户端上时前者一次都不会触发。
    /// </summary>
    private static Task<int> CountActiveUploadsAsync(AppDbContext db, CancellationToken ct) =>
        db.UploadSessions.CountAsync(s => UploadSessionStatuses.Active.Contains(s.Status), ct);

    /// <summary>全部项终结时收尾：completed / partial / failed，并回写计数</summary>
    private async Task FinalizeRunAsync(AppDbContext db, ExecutionRun run, DateTime now, CancellationToken ct)
    {
        var unfinished = run.Items.Count(i =>
            i.Status is ExecutionItemStatus.Pending or ExecutionItemStatus.Running);

        var succeeded = run.Items.Count(i => i.Status == ExecutionItemStatus.Succeeded);
        var failed = run.Items.Count(i =>
            i.Status is ExecutionItemStatus.Failed or ExecutionItemStatus.Timeout);

        var changed = run.SucceededItems != succeeded || run.FailedItems != failed;
        run.SucceededItems = succeeded;
        run.FailedItems = failed;

        if (unfinished == 0 && run.FinishedAt is null)
        {
            // 取消掉的执行保持 cancelled：它结束的原因是人取消了，不是跑完了
            if (run.Status != BatchStatus.Cancelled)
                run.Status = failed == 0
                    ? BatchStatus.Completed
                    : (succeeded > 0 ? BatchStatus.Partial : BatchStatus.Failed);

            run.FinishedAt = now;
            changed = true;

            _logger.LogInformation("执行 {RunId}（{Name}）结束：成功 {Succeeded}，失败 {Failed}，共 {Total} 项",
                run.Id, run.Name, succeeded, failed, run.TotalItems);
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }
}
