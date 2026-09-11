using System.Diagnostics.CodeAnalysis;
using BackupMonitor.Core.Entities.Execution;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
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
            {
                await StartPendingItemsAsync(db, commands, run, globalUploadLimit, now, ct);
                await ExpireStalePendingItemsAsync(db, alerting, run, now, ct);
            }

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

        var sessions = await FindSessionsAsync(db, item, command, startedAt, ct);
        if (sessions.Count == 0)
        {
            // 这个任务的上传正排在队列里等名额时，「没有新备份」这个结论是错的——
            // 恰恰相反，有新备份、只是还没轮到它传。宽限期一到就报成功的话，
            // 界面上这次计划显示「全部成功」，而一个字节都还没传。
            //
            // 判据走 UploadSlotCommandTypes 而不是 ConsumesUploadSlot(...)：EF 翻译不了
            // 表达式树里的方法调用，那一版会在运行时抛 InvalidOperationException，
            // 而这一行是「预检成功、上传会话还没建出来」的必经之路——也就是每一次
            // 正常的计划执行都会走到。异常从 AdvanceRunsAsync 一路抛到轮询的兜底 catch，
            // 那一轮**整个中止**：这一项永远停在「执行中」，同一次执行里排在后面的项
            // 一个都不放行，连带这一轮里其它执行（包括预检刚排出来的那次自动上传）
            // 也全部跳过。表现就是计划一直「执行中」而「传输中」一片空白，
            // 日志里只有一行「顺序执行器轮询异常」。
            var queuedUpload = await db.Set<ExecutionRunItem>().AnyAsync(i =>
                i.TaskId == item.TaskId
                && i.Id != item.Id
                && ExecutionQueueWaits.UploadSlotCommandTypes.Contains(i.CommandType)
                && (i.Status == ExecutionItemStatus.Pending || i.Status == ExecutionItemStatus.Running), ct);
            if (queuedUpload)
                return;

            // 预检自己已经把这一次的结论报上来了，不必再等一个宽限期去猜。
            var verdict = await ClassifyPrecheckAsync(db, command, ct);
            if (verdict is null)
                return;   // 还在等一件确定会发生的事（上传指令刚下发、会话还没建）

            if (verdict.Value.Status is { } concluded)
            {
                await FinishItemAsync(db, item, concluded, verdict.Value.Message!, now, ct);
                return;
            }

            // 老版本 Agent 不报清单，只能靠等：预检通过却一直没有上传会话，
            // 最常见的原因就是「这次压根没有新备份」。
            var since = command.CompletedAt ?? startedAt;
            if (now - since >= noUploadGrace)
                await FinishItemAsync(db, item, ExecutionItemStatus.Succeeded, "没有需要上传的新备份", now, ct);
            return;
        }

        // 界面上「点过去看」只能落到一个会话，取最新那个；结论则由全部会话汇总。
        item.UploadSessionId = sessions[^1].Id;

        // 还有任何一个在途就继续等——这一项的结论必须等所有单元都有了结果，
        // 否则先跑完的那个单元会替另外 17 个下结论。
        if (sessions.Any(s => UploadSessionStatuses.InFlight.Contains(s.Status)))
        {
            await db.SaveChangesAsync(ct);   // 只是把会话 ID 记下来，界面上能点过去
            return;
        }

        var committed = sessions.Count(s => s.Status == UploadStatus.Committed);
        if (committed == sessions.Count)
        {
            await FinishItemAsync(db, item, ExecutionItemStatus.Succeeded,
                sessions.Count == 1 ? "备份已入库" : $"{sessions.Count} 个单元全部入库", now, ct);
            return;
        }

        // 剩下的都是终态且至少有一个不是 committed。逐个单元把失败原因写出来：
        // 「5 个单元中 4 个失败」这句话本身就是这条缺陷此前吞掉的全部信息。
        var failed = sessions.Where(s => s.Status != UploadStatus.Committed).ToList();
        var reasons = string.Join("；", failed
            .GroupBy(s => EnumMapping.ToSnakeCase(s.Status))
            .Select(g => $"{g.Key} {g.Count()} 个"));

        await FinishItemAsync(db, item, ExecutionItemStatus.Failed,
            sessions.Count == 1
                ? $"上传{EnumMapping.ToSnakeCase(failed[0].Status)}"
                : $"{sessions.Count} 个单元中 {failed.Count} 个失败（{reasons}）",
            now, ct);
    }

    /// <summary>
    /// 预检回报出来的结论。
    ///
    /// 返回 (null, null) = 这条指令没有可用的清单（老版本 Agent），退回宽限期那条老路；
    /// 返回 null         = 有一件确定会发生的事还没发生（上传指令已下发，会话还没建出来），继续等；
    /// 其余              = 这一项此刻就能结案，连同要写进「说明」的那句话。
    ///
    /// 之所以要有这一步：原先这里唯一的判据是「等满 execution_queue_no_upload_grace_minutes
    /// （默认 10 分钟）还没有上传会话，就一律判成功、写『没有需要上传的新备份』」。
    /// 而清单里明明白白写着这次发生了什么，于是三种截然不同的情况被压成了同一句话：
    ///
    ///   一、真的没有新备份 —— 结论对，但那一项要在「执行中」上白挂十分钟，
    ///       严格顺序的计划里后面每一项都跟着顺延十分钟；
    ///   二、预检没通过（大小异常、缺必需文件、路径不存在）—— 判成功是彻头彻尾的假话。
    ///       告警那边已经按 size_abnormal / precheck_failed 报出去了，执行记录这边却写着成功，
    ///       两份记录对同一件事给出相反结论，人只会相信后者；
    ///   三、扫出了新备份但没有自动下发上传（任务不是自动模式，或这一份刚被人取消过上传）
    ///       —— 写「没有需要上传的新备份」会让人以为今天不用管它。
    ///
    /// 非通过状态的分界线与 AgentPrecheckService 的告警分界线取同一条（passed / no_new_backup
    /// 之外都算没通过）：两处对「这算不算故障」的定义一旦分家，就会出现告警中心在报警、
    /// 执行记录说成功这种没法排查的现象。
    /// </summary>
    private async Task<(ExecutionItemStatus? Status, string? Message)?> ClassifyPrecheckAsync(
        AppDbContext db, Core.Entities.Backup.Command command, CancellationToken ct)
    {
        var entries = PrecheckResultPayload.Entries(command.ResultPayload);
        if (entries.Count == 0)
            return (null, null);

        var passed = entries.Where(e => e.Status == PrecheckResultPayload.StatePassed).ToList();
        if (passed.Count == 0)
        {
            var noNew = EnumMapping.ToSnakeCase(PrecheckStatus.NoNewBackup);
            if (entries.All(e => e.Status == noNew))
                return (ExecutionItemStatus.Succeeded,
                    entries.Count == 1 ? "这次没有新备份" : $"{entries.Count} 个单元这次都没有新备份");

            var reasons = string.Join("；", entries
                .Where(e => e.Status != noNew)
                .GroupBy(e => e.Status)
                .Select(g => $"{DescribePrecheckStatus(g.Key)} {g.Count()} 个"));
            return (ExecutionItemStatus.Failed, $"预检没通过：{reasons}");
        }

        // 通过了、但一个上传都没下发出去。等下去也不会有会话，如实写清楚是哪一种。
        var live = passed.Where(e => e.UploadState
            is PrecheckResultPayload.StateDispatched
            or PrecheckResultPayload.StateQueued
            or PrecheckResultPayload.StateAlreadyRunning).ToList();
        if (live.Count == 0)
        {
            if (passed.All(e => e.UploadState == PrecheckResultPayload.StateAlreadyDone))
                return (ExecutionItemStatus.Succeeded,
                    passed.Count == 1 ? "这一份之前已经传完入库" : $"{passed.Count} 个单元之前都已传完入库");

            return (ExecutionItemStatus.Succeeded,
                $"扫出 {passed.Count} 份新备份，但没有自动下发上传"
                + "（任务不是自动模式，或这一份刚被取消过上传），需要人工发起");
        }

        // 上传指令已经下发了，会话还没建出来——正常情况下继续等就是了。
        // 但那条指令如果已经死了（客户端拒收、过期、失败），等下去只会耗满单项超时：
        // 一个立刻就能给出的准确失败，胜过 60 分钟后的一句「等超时」。
        var commandIds = live.Where(e => e.UploadCommandId is not null)
            .Select(e => e.UploadCommandId!.Value).ToList();
        if (commandIds.Count > 0)
        {
            var dead = await db.Commands.AsNoTracking()
                .Where(c => commandIds.Contains(c.Id) && DeadCommandStatuses.Contains(c.Status))
                .Select(c => new { c.Status, c.ResultMessage })
                .ToListAsync(ct);
            if (dead.Count == commandIds.Count)
                return (ExecutionItemStatus.Failed,
                    $"上传指令{EnumMapping.ToSnakeCase(dead[0].Status)}：{dead[0].ResultMessage ?? "无返回信息"}");
        }

        // 排队（queued）的那几个单元没有上传指令 ID——排进队列时指令还没建出来——
        // 上面那段检查够不着它们。而调用方进来之前已经确认「这个任务没有任何上传项
        // 还在 pending/running」，也就是排队的那些全部终结了：再等下去不会有任何变化。
        //
        // 少了这一段，这一项就挂满整个单项超时（计划默认 60 分钟）才被判失败，
        // 严格顺序时后面每一项都跟着顺延同样长的时间。会话找得到的情况已经在
        // FindSessionsAsync 里结案了，走到这里说明队列跑完确实没留下会话，
        // 那是个要让人看见的结果，不是「再等等」。
        // 只认 queued。dispatched 是「指令建出来了」，即便清单里没记下它的 ID
        // 也不能当成没人在跑——那一条仍然要等真会话。
        var queuedIds = live
            .Where(e => e.UploadState == PrecheckResultPayload.StateQueued && e.UploadCommandId is null)
            .Select(e => (Guid?)e.CandidateBackupSetId)
            .ToList();
        if (queuedIds.Count == live.Count)
        {
            var detail = await db.Set<ExecutionRunItem>().AsNoTracking()
                .Where(i => queuedIds.Contains(i.CandidateBackupSetId) && i.Message != null)
                .OrderByDescending(i => i.FinishedAt)
                .Select(i => i.Message)
                .FirstOrDefaultAsync(ct);

            return (ExecutionItemStatus.Failed,
                $"{live.Count} 份备份排进了上传队列，队列已经跑完却没有留下任何上传会话"
                + (string.IsNullOrWhiteSpace(detail) ? "" : $"（队列里那一项的结论：{detail}）"));
        }

        return null;
    }

    /// <summary>清单里存的是 snake_case，写进「说明」的要是人话。认不出来就原样放过去。</summary>
    private static string DescribePrecheckStatus(string snakeCase) =>
        EnumMapping.TryParseSnakeCase<PrecheckStatus>(snakeCase, out var status)
            ? PlainText.Of(status)
            : snakeCase;

    /// <summary>
    /// 这一项对应的**全部**上传会话。
    ///
    /// 返回集合而不是单个：一个任务可以有很多个业务单元（U8 一台机器 18 个账套是常态），
    /// 每个单元一个候选、一个会话。原先这里是 OrderByDescending(CreatedAt).FirstOrDefault()，
    /// 于是这一项的结论由「最后一个账套」单方面决定——最后一个成功就报成功，
    /// 另外 17 个失败一声不响，界面上这次计划显示「全部成功」。
    ///
    /// 三条定位路径，按精确度排：
    ///   一、项自带候选 ID（批量上传）——直接按它查；
    ///   二、预检项没有候选 ID（下发预检时候选还不存在），但预检回报的清单里
    ///       逐单元写着候选 ID，那才是这一项真正涉及的那几份备份；
    ///   三、都没有（老版本 Agent 不报清单）才退回时间窗。
    ///
    /// 第二条不是锦上添花，是必须的。只有时间窗那一版会漏掉**被复用的会话**：
    /// Agent 建会话的幂等键是候选键（AgentWorker.CreateUploadSessionAsync），
    /// 同一份备份先前已经传完入库时，服务端按键把那个 committed 的老会话原样交回，
    /// 不新建行。于是会话建于本项开跑之前，s.CreatedAt >= startedAt 查出 0 条——
    /// 上传明明在几秒内就结束了，这一项却要一直挂到单项超时（计划默认 60 分钟）
    /// 才被判失败，严格顺序时后面每一项都跟着顺延同样长的时间。
    /// </summary>
    private static async Task<List<Core.Entities.Upload.UploadSession>> FindSessionsAsync(
        AppDbContext db, ExecutionRunItem item, Core.Entities.Backup.Command? command, DateTime startedAt, CancellationToken ct)
    {
        var sessions = db.UploadSessions.AsNoTracking();

        if (item.CandidateBackupSetId is not null)
            return await sessions
                .Where(s => s.CandidateBackupSetId == item.CandidateBackupSetId)
                .OrderBy(s => s.CreatedAt)
                .ToListAsync(ct);

        var candidateIds = PrecheckResultPayload.Entries(command?.ResultPayload)
            .Select(e => e.CandidateBackupSetId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (candidateIds.Count > 0)
            return await sessions
                .Where(s => candidateIds.Contains(s.CandidateBackupSetId))
                .OrderBy(s => s.CreatedAt)
                .ToListAsync(ct);

        return await sessions
            .Where(s => s.TaskId == item.TaskId && s.CreatedAt >= startedAt)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
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

            // 全局上传闸只对真的会建上传会话的项生效。预检只是让客户端扫一遍目录算哈希，
            // 一个上传名额都不占；拿上传闸挡住它，等于「服务端有 4 个会话在传」
            // 就让所有计划连扫描都不许开始。而 Pending 项此前没有超时、
            // CreatePlanRunAsync 又规定「上一次没跑完就不再建新的」，
            // 于是这一挡是永久的——现场表现是计划停在「排队中」，此后再也不执行。
            if (ConsumesUploadSlot(item.CommandType))
            {
                var activeUploads = await CountActiveUploadsAsync(db, ct);
                if (activeUploads >= globalUploadLimit)
                {
                    _logger.LogInformation(
                        "全局上传并发已达上限（{Active}/{Limit}），执行 {RunId} 的剩余上传项继续排队",
                        activeUploads, globalUploadLimit, run.Id);
                    break;
                }
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

    /// <summary>
    /// 这一项下发之后会不会建上传会话，也就是它占不占全局上传名额。
    /// 判据与界面上那份队列名单共用一处（ExecutionQueueWaits）：
    /// 放行的人和显示的人对「在等什么」的定义分家，就会出现
    /// 「界面说有空余名额、队列却不放行」这种没法排查的现象。
    /// </summary>
    private static bool ConsumesUploadSlot(CommandType commandType) =>
        ExecutionQueueWaits.ConsumesUploadSlot(commandType);

    /// <summary>
    /// Pending 项的兜底超时。
    ///
    /// ItemTimeoutMinutes 原先只对 Running 项生效，Pending 项没有任何时限——
    /// 而计划不允许叠一次执行（CreatePlanRunAsync 见到未终结的 run 就返回 null），
    /// 所以一项永远排不上就等于这个计划永远不再执行，且没有任何告警。
    /// 上传闸修好之后这条路窄了很多，但「排队排到天荒地老」仍然要有个尽头：
    /// 判失败、发告警、让这次执行能收尾，下一次到点才建得出新的。
    ///
    /// 阈值取 2 倍单项超时：正常排队等的是别人跑完，一个单项超时的时长足够轮到它；
    /// 等了两倍还没轮到，说明名额没有在正常释放。
    ///
    /// 「等了多久」必须从**队列上一次推进**算起，不能从整次执行开始算，而且还有项在跑时
    /// 一律不判——这两条缺一条，这道兜底就会去杀正常但慢的执行：
    /// 计划默认单项超时 240 分钟、阈值 8 小时，而严格顺序的计划挂 5 个 U8 级任务
    /// （单个扫描+哈希+上传 2~3 小时）串行跑满 12 小时是完全正常的。
    /// 拿整次执行的开始时刻当基准的话，第 3 项还在正常跑，第 4、5 项就被判失败并发告警了。
    /// 要挡的是「名额不流转」，不是「跑得慢」——这两者的区别就是有没有项在推进。
    /// </summary>
    private async Task ExpireStalePendingItemsAsync(
        AppDbContext db, IAlertingService alerting, ExecutionRun run, DateTime now, CancellationToken ct)
    {
        // 有项在跑 = 队列在推进，后面的项只是还没轮到，不是排不上
        if (run.Items.Any(i => i.Status == ExecutionItemStatus.Running))
            return;

        var pendingLimit = TimeSpan.FromMinutes(run.ItemTimeoutMinutes * 2);

        // 起算点是最后一项终结的时刻——那才是当前这些 Pending 项开始「等不到」的时点。
        // 一项都还没终结时退回执行开始时刻（含跳过的项：它们建出来就是终态、
        // 带着 FinishedAt，不该被当成一次推进，但把起点抬到那一刻也没有坏处，
        // 因为放行是在同一轮里紧接着发生的）。
        var lastProgress = run.Items
            .Where(i => i.FinishedAt is not null)
            .Select(i => i.FinishedAt!.Value)
            .DefaultIfEmpty(run.StartedAt ?? run.CreatedAt)
            .Max();

        if (now - lastProgress < pendingLimit)
            return;

        foreach (var item in run.Items.Where(i => i.Status == ExecutionItemStatus.Pending).ToList())
        {
            ct.ThrowIfCancellationRequested();

            await FinishItemAsync(db, item, ExecutionItemStatus.Failed,
                $"队列已停滞超过 {pendingLimit.TotalMinutes:0} 分钟且没有任何项在跑，已判定失败让本次执行收尾", now, ct);

            var task = await db.BackupTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == item.TaskId, ct);
            await alerting.RaiseAsync(
                $"execution_item:{item.Id}:queue_timeout",
                AlertLevel.Warning,
                "execution_item_queue_timeout",
                $"备份计划里的这一项一直没能开始（{task?.Name ?? item.TaskId.ToString()}）",
                $"「{run.Name}」已经 {pendingLimit.TotalMinutes:0} 分钟没有任何一项在跑，这一项也始终没轮到，" +
                "多半是上传名额一直没有释放。这一项按失败处理，让这次执行结束——" +
                "否则这个计划下一次到点也不会执行。",
                clientId: item.ClientId,
                taskId: item.TaskId,
                ct: ct);
        }
    }

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

        var justFinished = false;
        if (unfinished == 0 && run.FinishedAt is null)
        {
            justFinished = true;
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

        if (justFinished)
            await EnqueuePlanFinishNoticeAsync(run, ct);
    }

    /// <summary>
    /// 备份计划完成回执：一次计划跑完发一条汇总，而不是每个任务各发一条。
    ///
    /// 一个 12 项的计划按任务发就是 12 条「都挺好」，而人要问的是
    /// 「昨晚那批跑完了吗，有没有翻车的」——一晚上一条就够，还能把失败的项列出来。
    ///
    /// 两条刻意的边界：
    /// **跑完就发**，不只在有失败时发——只在出事时发的摘要会退化成另一种告警，
    /// 而它不可替代的价值恰恰是那条「12 项全成功」：收到它才知道计划确实跑了。
    /// **只对到点触发的执行发**——人手点「立即执行」时正盯着页面看，再推一条是纯噪音。
    ///
    /// 用自己的作用域：这是收尾之后的附带动作，发不出去不该影响执行记录本身。
    /// </summary>
    private async Task EnqueuePlanFinishNoticeAsync(ExecutionRun run, CancellationToken ct)
    {
        try
        {
            if (run.PlanId is null)
                return;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var plan = await db.BackupPlans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == run.PlanId.Value, ct);
            if (!ShouldNotifyPlanFinish(run, plan))
                return;

            var failedItems = await db.ExecutionRunItems.AsNoTracking()
                .Where(i => i.RunId == run.Id
                            && (i.Status == ExecutionItemStatus.Failed
                                || i.Status == ExecutionItemStatus.Timeout))
                .OrderBy(i => i.SortOrder)
                .Select(i => new PlanFinishFailure(
                    i.Task.Client.DisplayName,
                    i.Task.Name,
                    i.Status,
                    i.Message))
                .ToListAsync(ct);

            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var channels = await notifications.GetSettingsAsync(ct);

            if (NotificationNotice.Enqueue(
                    db, channels,
                    BuildPlanFinishSubject(plan.Name),
                    BuildPlanFinishBody(plan.Name, run, failedItems),
                    AlertCategoryCatalog.PlanFinished) > 0)
            {
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // 回执发不出去不能影响执行记录本身——那份记录才是这次计划到底跑成什么样的依据。
            _logger.LogWarning(ex, "备份计划完成回执入队失败 runId={RunId}", run.Id);
        }
    }

    /// <summary>
    /// 这次执行要不要发完成回执。
    ///
    /// 单拎出来是因为它错了不会报错，只会静默地多发或少发——而"群里今天怎么多了/少了一条"
    /// 这种事没人会去追。三个条件缺一不可：
    /// 计划驱动的（不是批量上传那种没有计划的执行）、到点触发的（不是人手点的立即执行）、
    /// 而且这个计划确实开了回执。
    /// </summary>
    internal static bool ShouldNotifyPlanFinish(
        ExecutionRun run,
        [NotNullWhen(true)] Core.Entities.Backup.BackupPlan? plan)
    {
        if (run.PlanId is null || plan is null || !plan.NotifyOnFinish)
            return false;

        // 人手点「立即执行」时正盯着页面看，再推一条到群里是纯噪音。
        return string.Equals(run.TriggerSource, "schedule", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>回执里列出的一条失败项。</summary>
    internal sealed record PlanFinishFailure(
        string ClientName, string TaskName, ExecutionItemStatus Status, string? Message);

    /// <summary>失败项最多列几条。</summary>
    internal const int MaxListedFailures = 20;

    internal static string BuildPlanFinishSubject(string planName) => $"备份计划完成：{planName}";

    /// <summary>
    /// 回执正文。
    ///
    /// 排版是按「在手机上扫一眼」定的，不是按「在电脑上细看」：
    /// 前四行把结论说完（哪个计划、什么结果、几成功几失败、跑了多久），
    /// 失败的项列在后面——那是唯一需要人动手的部分。
    ///
    /// 失败项最多列 <see cref="MaxListedFailures"/> 条。再多也不会有人在手机上一条条看，
    /// 而「一共失败了几条」这个关键数字第三行已经给全了，剩下的去管理页面看执行详情。
    /// </summary>
    internal static string BuildPlanFinishBody(
        string planName, ExecutionRun run, IReadOnlyList<PlanFinishFailure> failures)
    {
        var outcome = run.Status switch
        {
            BatchStatus.Completed => "全部成功",
            BatchStatus.Partial => "部分成功",
            BatchStatus.Failed => "全部失败",
            BatchStatus.Cancelled => "已取消",
            _ => EnumMapping.ToSnakeCase(run.Status)
        };

        var lines = new List<string>
        {
            $"计划: {planName}",
            $"结果: {outcome}",
            $"共 {run.TotalItems} 项，成功 {run.SucceededItems}，失败 {run.FailedItems}",
            $"开始: {ToLocal(run.StartedAt)}    结束: {ToLocal(run.FinishedAt)}"
        };

        if (failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("失败的项：");
            foreach (var failure in failures.Take(MaxListedFailures))
            {
                // 超时单独说：它和「跑了但失败了」在处置上完全不同——
                // 前者多半是机器关着或者网断了，后者才需要去看备份本身。
                var reason = failure.Status == ExecutionItemStatus.Timeout
                    ? "超时"
                    : Shorten(failure.Message) ?? "失败";
                lines.Add($"  · {failure.ClientName} / {failure.TaskName}（{reason}）");
            }

            if (failures.Count > MaxListedFailures)
                lines.Add($"  …… 另有 {failures.Count - MaxListedFailures} 项，详见管理页面");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ToLocal(DateTime? utc) =>
        utc is null ? "—" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>失败原因截到一行能看完的长度：整段堆栈塞进钉钉消息没人会读。</summary>
    private static string? Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var single = message.ReplaceLineEndings(" ").Trim();
        return single.Length <= 60 ? single : single[..60] + "…";
    }
}
