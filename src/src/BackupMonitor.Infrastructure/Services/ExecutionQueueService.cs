using System.Text.Json;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Execution;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 顺序执行队列（V028）：备份计划到点执行与批量上传共用的那一套。
///
/// 这里只负责「把一次执行连同它的每一项建出来」和「读」，真正的推进
/// （放行下一项、判定终结、超时）在 SequentialExecutionWorker 里——
/// 建队列是请求线程的事，推进是后台的事，两者不该纠缠在一起。
/// </summary>
public interface IExecutionQueueService
{
    /// <summary>
    /// 按计划建一次执行。计划里一个任务都没有，或它已经有一次在跑，返回 null。
    /// </summary>
    Task<ExecutionRun?> CreatePlanRunAsync(
        Guid planId, DateTime? scheduledFor, string triggerSource, Guid? triggeredBy, CancellationToken ct = default);

    Task<ExecutionRunDto> GetRunAsync(Guid runId, CancellationToken ct = default);

    Task<PagedResult<ExecutionRunDto>> ListRunsAsync(Guid? planId, PagedQuery query, CancellationToken ct = default);

    /// <summary>
    /// 此刻还没跑完的那些项，连同「它在等什么」。数字（queue-status）回答不了
    /// 「什么时候轮到我」，这份名单才能——顺序与执行器放行的顺序是同一口径。
    /// </summary>
    Task<ExecutionQueueSnapshotDto> GetQueueAsync(CancellationToken ct = default);

    /// <summary>
    /// 为一批手动「立即备份」建一次执行（D3）。并发度取系统设置 manual_run_max_concurrent，
    /// 而不是给每个任务各发一条独立指令——那样十个任务就是十个同时开传。
    /// 全部任务都不可执行时返回 null。
    /// </summary>
    Task<ExecutionRun?> CreateManualRunAsync(
        IReadOnlyCollection<Guid> taskIds, Guid? triggeredBy, CancellationToken ct = default);

    /// <summary>
    /// 全局上传名额已满时，把一次上传排进队列而不是当场下发（D3）。
    /// 返回 false 表示还有名额、调用方应当照旧直接下发。
    ///
    /// 闸不能放在建会话那一步：Agent 对 UPLOAD_SESSION_CONFLICT 没有退避重试，
    /// 在那里 409 等于把限流变成备份失败。
    /// </summary>
    Task<bool> TryDeferUploadAsync(
        Guid clientId, Guid taskId, Guid candidateBackupSetId, CancellationToken ct = default);

    /// <summary>
    /// 取消一次执行，并且真的停住：还没开跑的项置 cancelled，在跑的项取消它的指令、
    /// 取消这次执行已产生的在途上传会话，并给相关候选打上取消标记。
    /// 少了后面那几步，「取消」的实际行为是「在跑的预检继续扫，扫完照样自动下发全部上传」。
    /// </summary>
    Task<ExecutionRunDto> CancelRunAsync(Guid runId, CancellationToken ct = default);
}

public class ExecutionQueueService : IExecutionQueueService
{
    /// <summary>还没终结的执行（用于「这个计划已经有一次在跑」的判定）</summary>
    public static readonly BatchStatus[] ActiveRunStatuses = [BatchStatus.Pending, BatchStatus.Running];

    /// <summary>手动执行的并发度 system_settings 键</summary>
    public const string ManualMaxConcurrentKey = "manual_run_max_concurrent";

    /// <summary>单项超时（分钟）：与备份计划的默认值同口径</summary>
    private const int ManualItemTimeoutMinutes = 120;

    private readonly AppDbContext _db;
    private readonly IAuditRecorder _audit;
    private readonly SystemSettingsProvider _settings;

    /// <summary>取消一次执行时要记下「是谁取消的」（写进候选的 cancelled_by）</summary>
    private readonly ICurrentContext _context;

    private readonly ILogger<ExecutionQueueService> _logger;

    public ExecutionQueueService(
        AppDbContext db,
        IAuditRecorder audit,
        SystemSettingsProvider settings,
        ICurrentContext context,
        ILogger<ExecutionQueueService> logger)
    {
        _db = db;
        _audit = audit;
        _settings = settings;
        _context = context;
        _logger = logger;
    }

    public async Task<ExecutionRun?> CreateManualRunAsync(
        IReadOnlyCollection<Guid> taskIds, Guid? triggeredBy, CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return null;

        var tasks = await _db.BackupTasks
            .Include(t => t.Client)
            .Where(t => taskIds.Contains(t.Id))
            .ToListAsync(ct);

        // 一个都跑不了就别建一条空执行：界面上多出一行「0 项」除了让人困惑没有别的作用，
        // 调用方拿到 null 会退回逐个下发的老路径，那条路径会把每个任务各自的原因说清楚。
        if (tasks.Count == 0 || tasks.All(t => SkipReason(t) is not null))
            return null;

        var now = DateTime.UtcNow;
        var run = new ExecutionRun
        {
            Id = Guid.NewGuid(),
            Kind = ExecutionRunKind.Manual,
            Name = $"手动立即备份（{tasks.Count} 个任务）",
            Status = BatchStatus.Pending,
            MaxConcurrent = Math.Clamp(await _settings.GetIntAsync(ManualMaxConcurrentKey, 2, ct), 1, 32),
            ItemTimeoutMinutes = ManualItemTimeoutMinutes,
            TriggerSource = "manual",
            TriggeredBy = triggeredBy,
            CreatedAt = now
        };

        var sort = 0;
        // 按调用方给的顺序放行，而不是按 Guid：人在界面上勾选的顺序就是他心里的优先次序。
        foreach (var taskId in taskIds)
        {
            var task = tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null)
                continue;

            var skipReason = SkipReason(task);
            run.Items.Add(new ExecutionRunItem
            {
                Id = Guid.NewGuid(),
                SortOrder = sort++,
                ClientId = task.ClientId,
                TaskId = task.Id,
                CommandType = CommandType.PrecheckTask,
                Status = skipReason is null ? ExecutionItemStatus.Pending : ExecutionItemStatus.Skipped,
                Message = skipReason,
                FinishedAt = skipReason is null ? null : now
            });
        }

        run.TotalItems = run.Items.Count;
        _db.ExecutionRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("task.batch_backup_now", AuditResult.Success, "execution_run", run.Id,
            afterData: JsonSerializer.Serialize(new { runId = run.Id, run.TotalItems, run.MaxConcurrent }), ct: ct);

        _logger.LogInformation("手动批量立即备份已产生一次执行 {RunId}：共 {Total} 项，并发度 {Concurrent}",
            run.Id, run.TotalItems, run.MaxConcurrent);

        return run;
    }

    public async Task<ExecutionRun?> CreatePlanRunAsync(
        Guid planId, DateTime? scheduledFor, string triggerSource, Guid? triggeredBy, CancellationToken ct = default)
    {
        var plan = await _db.BackupPlans
            .Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new NotFoundException("备份计划", planId);

        // 上一次还没跑完就不再叠一次：顺序执行的意义就在于同一时刻只有约定好的那几项在动
        var hasActive = await _db.ExecutionRuns
            .AnyAsync(r => r.PlanId == planId && ActiveRunStatuses.Contains(r.Status), ct);
        if (hasActive)
        {
            _logger.LogWarning("备份计划 {Plan} 上一次执行尚未结束，{Due} 这一次跳过",
                plan.Name, scheduledFor);
            await MarkDueHandledAsync(plan, scheduledFor, ct);
            return null;
        }

        var taskIds = plan.Items.OrderBy(i => i.SortOrder).Select(i => i.TaskId).ToList();
        if (taskIds.Count == 0)
        {
            _logger.LogInformation("备份计划 {Plan} 里没有任何任务，{Due} 这一次不产生执行记录",
                plan.Name, scheduledFor);
            await MarkDueHandledAsync(plan, scheduledFor, ct);
            return null;
        }

        var tasks = await _db.BackupTasks
            .Include(t => t.Client)
            .Where(t => taskIds.Contains(t.Id))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var run = new ExecutionRun
        {
            Id = Guid.NewGuid(),
            Kind = ExecutionRunKind.BackupPlan,
            PlanId = plan.Id,
            Name = plan.Name,
            Status = BatchStatus.Pending,
            MaxConcurrent = plan.MaxConcurrent,
            ItemTimeoutMinutes = plan.ItemTimeoutMinutes,
            TriggerSource = triggerSource,
            TriggeredBy = triggeredBy,
            ScheduledFor = scheduledFor,
            CreatedAt = now
        };

        var sort = 0;
        foreach (var taskId in taskIds)
        {
            var task = tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null)
                continue;

            var skipReason = SkipReason(task);
            run.Items.Add(new ExecutionRunItem
            {
                Id = Guid.NewGuid(),
                SortOrder = sort++,
                ClientId = task.ClientId,
                TaskId = task.Id,
                CommandType = CommandType.PrecheckTask,
                // 跳过的项一开始就是终态：它们仍然要出现在执行记录里，
                // 否则「今晚这个任务为什么没备份」在界面上无处可查。
                Status = skipReason is null ? ExecutionItemStatus.Pending : ExecutionItemStatus.Skipped,
                Message = skipReason,
                FinishedAt = skipReason is null ? null : now
            });
        }

        run.TotalItems = run.Items.Count;
        _db.ExecutionRuns.Add(run);

        plan.LastRunAt = now;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("backup_plan.run", AuditResult.Success, "backup_plan", plan.Id,
            afterData: JsonSerializer.Serialize(new { runId = run.Id, run.TotalItems, triggerSource }), ct: ct);

        _logger.LogInformation("备份计划 {Plan} 已产生一次执行 {RunId}：共 {Total} 项，并发度 {Concurrent}",
            plan.Name, run.Id, run.TotalItems, run.MaxConcurrent);

        return run;
    }

    /// <summary>
    /// 把「这一次到期已经处理过了」记下来。
    ///
    /// 两条 return null 的分支原先什么都不写，于是 PromoteDuePlansAsync 的判据
    /// （last_run_at 早于 due）一直成立：执行器每 15 秒重算一次、重新走到这里、
    /// 重新打一条同样的日志，一整晚刷几千行。附带的第二个后果更麻烦——
    /// 卡住的那次执行一结束，同一个到期时刻立刻就被补跑一次，
    /// 而那个时刻可能已经是白天的业务高峰了。
    ///
    /// 只在 scheduledFor 有值（按计划触发）时推进；手动「立即执行」传的是 null，
    /// 它不该影响下一次到点的判定。
    /// </summary>
    private async Task MarkDueHandledAsync(BackupPlan plan, DateTime? scheduledFor, CancellationToken ct)
    {
        if (scheduledFor is null || (plan.LastRunAt is not null && plan.LastRunAt >= scheduledFor))
            return;

        await _db.BackupPlans
            .Where(p => p.Id == plan.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastRunAt, scheduledFor.Value), ct);
    }

    public async Task<bool> TryDeferUploadAsync(
        Guid clientId, Guid taskId, Guid candidateBackupSetId, CancellationToken ct = default)
    {
        // 同一个候选已经排着了就别再排一次：预检结果可能被重复上报（指令重试、
        // Agent 重启后补报），每次都排一条会让同一份备份传两遍。
        var alreadyQueued = await _db.Set<ExecutionRunItem>().AnyAsync(i =>
            i.CandidateBackupSetId == candidateBackupSetId
            && (i.Status == ExecutionItemStatus.Pending || i.Status == ExecutionItemStatus.Running), ct);
        if (alreadyQueued)
            return true;

        if (!await ShouldQueueUploadAsync(taskId, ct))
            return false;

        var now = DateTime.UtcNow;
        var run = new ExecutionRun
        {
            Id = Guid.NewGuid(),
            Kind = ExecutionRunKind.Manual,
            Name = "排队等待上传",
            Status = BatchStatus.Pending,
            MaxConcurrent = 1,
            ItemTimeoutMinutes = ManualItemTimeoutMinutes,
            TriggerSource = "auto_upload_queued",
            CreatedAt = now,
            TotalItems = 1
        };
        run.Items.Add(new ExecutionRunItem
        {
            Id = Guid.NewGuid(),
            SortOrder = 0,
            ClientId = clientId,
            TaskId = taskId,
            CandidateBackupSetId = candidateBackupSetId,
            CommandType = CommandType.UploadCandidate,
            Status = ExecutionItemStatus.Pending
        });

        _db.ExecutionRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("候选 {Candidate} 的上传已排进队列等待放行", candidateBackupSetId);
        return true;
    }

    /// <summary>
    /// 这次自动上传该不该排队，而不是当场下发。两个理由，任一成立就排队：
    ///
    /// 一、这次预检本身是执行队列下发的。多单元任务的上传必须回到队列里按名额放行——
    ///     预检结果一到就逐个单元当场下发，完全绕开了执行的并发度，
    ///     所以「严格顺序（1）」对 U8 那种一台机器 18 个账套的任务名不副实：
    ///     一次预检回来 18 个单元同时开传。
    ///     刻意排进一次**独立**的执行，而不是塞回原来那次：原执行的预检项还在 Running，
    ///     并发度 1 时它会把新加的上传项挡在门外，而它自己又在等这些上传产生的会话——
    ///     那是一个谁也动不了的死锁。
    ///
    /// 二、全局上传名额已经满了（D3 原有判据）。
    /// </summary>
    private async Task<bool> ShouldQueueUploadAsync(Guid taskId, CancellationToken ct)
    {
        var queueDriven = await _db.Set<ExecutionRunItem>().AnyAsync(i =>
            i.TaskId == taskId
            && i.CommandType == CommandType.PrecheckTask
            && (i.Status == ExecutionItemStatus.Pending || i.Status == ExecutionItemStatus.Running), ct);
        if (queueDriven)
            return true;

        var limit = Math.Clamp(
            await _settings.GetIntAsync(SequentialExecutionWorker.GlobalUploadLimitKey, 4, ct), 1, 64);
        var active = await _db.UploadSessions
            .CountAsync(s => UploadSessionStatuses.Active.Contains(s.Status), ct);
        return active >= limit;
    }

    /// <summary>不参与执行的任务与它的原因；null 表示可以执行</summary>
    /// <summary>
    /// 这个任务此刻能不能真的跑起来；null = 能跑。
    ///
    /// 公开是因为漏备份巡检要用同一份判据：那边判的是「该跑却没跑成」，
    /// 两边对「该不该跑」的定义一旦分家，就会出现「队列认为这个任务根本不该下发、
    /// 巡检却在为它报漏备份」这种永远不会恢复的告警。调用方需要 task.Client 已加载。
    /// </summary>
    public static string? SkipReason(BackupTask task)
    {
        if (!task.Enabled)
            return "任务已停用";
        if (task.TaskMode is TaskMode.Paused)
            return "任务处于暂停状态";
        if (task.TaskMode is TaskMode.MonitorOnly)
            return "任务是「仅监控」模式，不上传备份";
        if (task.Client.Status is ClientStatus.Disabled or ClientStatus.Revoked or ClientStatus.PendingApproval)
            return "客户端不可用（已禁用/注销/未审批）";
        return null;
    }

    public async Task<ExecutionRunDto> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.ExecutionRuns.AsNoTracking()
            .Include(r => r.Plan)
            .Include(r => r.Items).ThenInclude(i => i.Task).ThenInclude(t => t.Client)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new NotFoundException("执行记录", runId);

        return Map(run, withItems: true);
    }

    public async Task<PagedResult<ExecutionRunDto>> ListRunsAsync(
        Guid? planId, PagedQuery query, CancellationToken ct = default)
    {
        var runs = _db.ExecutionRuns.AsNoTracking().Include(r => r.Plan).AsQueryable();
        if (planId is not null)
            runs = runs.Where(r => r.PlanId == planId);

        var totalCount = await runs.LongCountAsync(ct);
        var items = await runs
            .OrderByDescending(r => r.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return PagedResult<ExecutionRunDto>.Create(
            items.Select(r => Map(r, withItems: false)).ToList(), totalCount, query.Page, query.PageSize);
    }

    public async Task<ExecutionQueueSnapshotDto> GetQueueAsync(CancellationToken ct = default)
    {
        // 只看还没终结的执行里还没终结的项：跑完的那些属于「运行记录」，
        // 混进队列名单里会把「还要等几个」这个唯一有用的数字冲淡。
        var items = await _db.Set<ExecutionRunItem>().AsNoTracking()
            .Include(i => i.Run)
            .Include(i => i.Task).ThenInclude(t => t.Client)
            .Where(i => (i.Status == ExecutionItemStatus.Pending || i.Status == ExecutionItemStatus.Running)
                && (i.Run.Status == BatchStatus.Pending || i.Run.Status == BatchStatus.Running))
            .ToListAsync(ct);

        var activeUploads = await _db.UploadSessions
            .CountAsync(s => UploadSessionStatuses.Active.Contains(s.Status), ct);
        var globalLimit = Math.Clamp(
            await _settings.GetIntAsync(SequentialExecutionWorker.GlobalUploadLimitKey, 4, ct), 1, 64);
        var slotFull = activeUploads >= globalLimit;

        var runningPerRun = items
            .Where(i => i.Status == ExecutionItemStatus.Running)
            .GroupBy(i => i.RunId)
            .ToDictionary(g => g.Key, g => g.Count());

        var entries = items
            // 在跑的排最前，其余按「执行产生的先后 + 执行内的顺序号」——
            // 执行器就是这么走的（runs 按 CreatedAt、items 按 SortOrder），
            // 名单的顺序一旦与放行顺序不一致，它就回答不了「还要等几个」。
            .OrderByDescending(i => i.Status == ExecutionItemStatus.Running)
            .ThenBy(i => i.Run.CreatedAt)
            .ThenBy(i => i.SortOrder)
            .Select(i => new ExecutionQueueEntryDto
            {
                ItemId = i.Id,
                RunId = i.RunId,
                RunName = i.Run.Name,
                RunKind = EnumMapping.ToSnakeCase(i.Run.Kind),
                TriggerSource = i.Run.TriggerSource,
                RunMaxConcurrent = i.Run.MaxConcurrent,
                SortOrder = i.SortOrder,
                TaskId = i.TaskId,
                TaskName = i.Task?.Name ?? "",
                ClientId = i.ClientId,
                ClientName = ClientLabel(i),
                CommandType = EnumMapping.ToSnakeCase(i.CommandType),
                Status = EnumMapping.ToSnakeCase(i.Status),
                Wait = i.Status == ExecutionItemStatus.Running
                    ? ExecutionQueueWaits.Running
                    : ExecutionQueueWaits.Classify(
                        runningPerRun.TryGetValue(i.RunId, out var n) ? n : 0,
                        i.Run.MaxConcurrent,
                        i.CommandType,
                        slotFull),
                RunCreatedAt = i.Run.CreatedAt,
                StartedAt = i.StartedAt
            })
            .ToList();

        return new ExecutionQueueSnapshotDto
        {
            ActiveUploads = activeUploads,
            GlobalLimit = globalLimit,
            RunningItems = entries.Count(e => e.Wait == ExecutionQueueWaits.Running),
            WaitingForUploadSlot = entries.Count(e => e.Wait == ExecutionQueueWaits.WaitingForUploadSlot),
            WaitingInRun = entries.Count(e => e.Wait == ExecutionQueueWaits.WaitingInRun),
            Items = entries
        };
    }

    public async Task<ExecutionRunDto> CancelRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.ExecutionRuns
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new NotFoundException("执行记录", runId);

        if (!ActiveRunStatuses.Contains(run.Status))
            throw new BusinessException("CONFLICT", "这次执行已经结束，不能取消", 409);

        var now = DateTime.UtcNow;
        foreach (var item in run.Items.Where(i => i.Status == ExecutionItemStatus.Pending))
        {
            item.Status = ExecutionItemStatus.Cancelled;
            item.Message = "执行被取消";
            item.FinishedAt = now;
        }

        // 在跑的那几项也要真的停住。原先这里只处理 Pending 项、把在跑的交给它们自己跑完，
        // 于是「取消这次执行」的实际行为是：在跑的预检继续扫，扫完照样自动下发全部单元的上传。
        // 人点取消想要的是「别再传了」，拿到的却是「再等十分钟然后开始传」。
        var runningItems = run.Items.Where(i => i.Status == ExecutionItemStatus.Running).ToList();
        var commandIds = runningItems.Where(i => i.CommandId is not null).Select(i => i.CommandId!.Value).ToList();
        var taskIds = runningItems.Select(i => i.TaskId).Distinct().ToList();
        var startedAt = runningItems.Count == 0
            ? now
            : runningItems.Min(i => i.StartedAt ?? run.StartedAt ?? run.CreatedAt);

        var cancelledCommands = 0;
        if (commandIds.Count > 0)
        {
            // 还没被客户端跑完的指令直接置 cancelled；已经终结的不动
            cancelledCommands = await _db.Commands
                .Where(c => commandIds.Contains(c.Id)
                    && (c.Status == CommandStatus.Pending
                        || c.Status == CommandStatus.Claimed
                        || c.Status == CommandStatus.Running))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, CommandStatus.Cancelled)
                    .SetProperty(c => c.CompletedAt, now)
                    .SetProperty(c => c.ResultMessage, "这次执行被管理员取消"), ct);
        }

        // 这次执行已经产生的、还在途的上传会话一并取消
        var cancelledSessions = 0;
        var cancelledCandidates = 0;
        if (taskIds.Count > 0)
        {
            var sessions = await _db.UploadSessions
                .Where(s => taskIds.Contains(s.TaskId)
                    && s.CreatedAt >= startedAt
                    && UploadSessionStatuses.InFlight.Contains(s.Status))
                .ToListAsync(ct);

            foreach (var session in sessions)
            {
                session.Status = UploadStatus.Cancelled;
                session.CompletedAt = now;
                session.UpdatedAt = now;
            }
            cancelledSessions = sessions.Count;

            // 候选上也要打标记，否则预检回来之后自动下发会把这些上传原样再发一遍——
            // 「取消」在那条路径上是看不见的，它只认「有没有已入库的痕迹」。
            var candidates = await _db.CandidateBackupSets
                .Where(c => taskIds.Contains(c.TaskId)
                    && c.CancelledAt == null
                    && (c.PrecheckedAt >= startedAt || sessions.Select(s => s.CandidateBackupSetId).Contains(c.Id)))
                .ToListAsync(ct);

            foreach (var candidate in candidates)
            {
                candidate.CancelledAt = now;
                candidate.CancelledBy = _context.UserId;
                candidate.UpdatedAt = now;
            }
            cancelledCandidates = candidates.Count;
        }

        foreach (var item in runningItems)
        {
            item.Status = ExecutionItemStatus.Cancelled;
            item.Message = "执行被取消";
            item.FinishedAt = now;
        }

        run.Status = BatchStatus.Cancelled;
        run.FinishedAt = now;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("execution_run.cancel", AuditResult.Success, "execution_run", run.Id,
            afterData: JsonSerializer.Serialize(new
            {
                cancelledItems = runningItems.Count,
                cancelledCommands,
                cancelledSessions,
                cancelledCandidates
            }), ct: ct);

        _logger.LogInformation(
            "执行 {RunId} 已取消：停掉 {Items} 个在跑的项、{Commands} 条指令、{Sessions} 个上传会话，"
            + "并给 {Candidates} 个候选打上取消标记",
            run.Id, runningItems.Count, cancelledCommands, cancelledSessions, cancelledCandidates);

        return await GetRunAsync(runId, ct);
    }

    internal static ExecutionRunDto Map(ExecutionRun run, bool withItems) => new()
    {
        Id = run.Id,
        Kind = EnumMapping.ToSnakeCase(run.Kind),
        PlanId = run.PlanId,
        PlanName = run.Plan?.Name,
        UploadBatchId = run.UploadBatchId,
        Name = run.Name,
        Status = EnumMapping.ToSnakeCase(run.Status),
        MaxConcurrent = run.MaxConcurrent,
        ItemTimeoutMinutes = run.ItemTimeoutMinutes,
        TriggerSource = run.TriggerSource,
        TotalItems = run.TotalItems,
        SucceededItems = run.SucceededItems,
        FailedItems = run.FailedItems,
        ScheduledFor = run.ScheduledFor,
        CreatedAt = run.CreatedAt,
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        Items = withItems
            ? run.Items.OrderBy(i => i.SortOrder).Select(i => new ExecutionRunItemDto
            {
                Id = i.Id,
                SortOrder = i.SortOrder,
                TaskId = i.TaskId,
                TaskName = i.Task?.Name ?? "",
                ClientId = i.ClientId,
                ClientName = ClientLabel(i),
                Status = EnumMapping.ToSnakeCase(i.Status),
                CommandId = i.CommandId,
                UploadSessionId = i.UploadSessionId,
                StartedAt = i.StartedAt,
                FinishedAt = i.FinishedAt,
                Message = i.Message
            }).ToList()
            : []
    };

    private static string ClientLabel(ExecutionRunItem item)
    {
        var client = item.Task?.Client;
        if (client is null)
            return "";
        return string.IsNullOrWhiteSpace(client.DisplayName) ? client.Hostname : client.DisplayName;
    }
}
