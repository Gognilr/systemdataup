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

    /// <summary>取消一次执行：还没开跑的项一律置 cancelled，已经在跑的项交给它自己跑完</summary>
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
    private readonly ILogger<ExecutionQueueService> _logger;

    public ExecutionQueueService(
        AppDbContext db, IAuditRecorder audit, SystemSettingsProvider settings, ILogger<ExecutionQueueService> logger)
    {
        _db = db;
        _audit = audit;
        _settings = settings;
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
            _logger.LogWarning("备份计划 {Plan} 上一次执行尚未结束，本次触发跳过", plan.Name);
            return null;
        }

        var taskIds = plan.Items.OrderBy(i => i.SortOrder).Select(i => i.TaskId).ToList();
        if (taskIds.Count == 0)
        {
            _logger.LogInformation("备份计划 {Plan} 里没有任何任务，不产生执行记录", plan.Name);
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

    public async Task<bool> TryDeferUploadAsync(
        Guid clientId, Guid taskId, Guid candidateBackupSetId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(
            await _settings.GetIntAsync(SequentialExecutionWorker.GlobalUploadLimitKey, 4, ct), 1, 64);
        var active = await _db.UploadSessions
            .CountAsync(s => UploadSessionStatuses.Active.Contains(s.Status), ct);
        if (active < limit)
            return false;

        // 同一个候选已经排着了就别再排一次：预检结果可能被重复上报（指令重试、
        // Agent 重启后补报），每次都排一条会让同一份备份传两遍。
        var alreadyQueued = await _db.Set<ExecutionRunItem>().AnyAsync(i =>
            i.CandidateBackupSetId == candidateBackupSetId
            && (i.Status == ExecutionItemStatus.Pending || i.Status == ExecutionItemStatus.Running), ct);
        if (alreadyQueued)
            return true;

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

        _logger.LogInformation(
            "全局上传并发已达上限（{Limit}），候选 {Candidate} 排队等待名额", limit, candidateBackupSetId);
        return true;
    }

    /// <summary>不参与执行的任务与它的原因；null 表示可以执行</summary>
    private static string? SkipReason(BackupTask task)
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

        // 已经在跑的那几项交给它们自己跑完：中途掐断上传只会留下半份数据，
        // 而队列的作用是「不再放行新的」。
        var stillRunning = run.Items.Any(i => i.Status == ExecutionItemStatus.Running);
        run.Status = BatchStatus.Cancelled;
        run.FinishedAt = stillRunning ? null : now;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("execution_run.cancel", AuditResult.Success, "execution_run", run.Id,
            afterData: JsonSerializer.Serialize(new { stillRunning }), ct: ct);

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
