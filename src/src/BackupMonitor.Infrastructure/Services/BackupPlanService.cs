using System.Text.Json;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 备份计划（V028）：把多个任务编成一组，到点按顺序执行。
///
/// 一个任务只能属于一个计划，且进了计划之后它自己的扫描计划不再下发给 Agent
/// （AgentConfigService 会把 ScanSchedule 置空）——否则同一个任务一天会跑两次：
/// 一次是 Agent 按 cron 自己触发的，一次是服务端的计划驱动的。
/// </summary>
public interface IBackupPlanService
{
    Task<List<BackupPlanDto>> GetListAsync(CancellationToken ct = default);
    Task<BackupPlanDto> GetAsync(Guid planId, CancellationToken ct = default);
    Task<BackupPlanDto> CreateAsync(BackupPlanUpsertDto request, CancellationToken ct = default);
    Task<BackupPlanDto> UpdateAsync(Guid planId, BackupPlanUpsertDto request, CancellationToken ct = default);
    Task DeleteAsync(Guid planId, CancellationToken ct = default);

    /// <summary>立即执行一次（不影响下一次到点执行）</summary>
    Task<ExecutionRunDto> TriggerAsync(Guid planId, CancellationToken ct = default);
}

public class BackupPlanService : IBackupPlanService
{
    private readonly AppDbContext _db;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly IExecutionQueueService _queue;
    private readonly ILogger<BackupPlanService> _logger;

    public BackupPlanService(
        AppDbContext db,
        ICurrentContext context,
        IAuditRecorder audit,
        IExecutionQueueService queue,
        ILogger<BackupPlanService> logger)
    {
        _db = db;
        _context = context;
        _audit = audit;
        _queue = queue;
        _logger = logger;
    }

    public async Task<List<BackupPlanDto>> GetListAsync(CancellationToken ct = default)
    {
        var plans = await LoadPlansQuery().ToListAsync(ct);

        var planIds = plans.Select(p => p.Id).ToList();
        var activeRuns = await _db.ExecutionRuns.AsNoTracking()
            .Where(r => r.PlanId != null && planIds.Contains(r.PlanId.Value)
                && ExecutionQueueService.ActiveRunStatuses.Contains(r.Status))
            .ToListAsync(ct);

        return plans
            .Select(p => Map(p, activeRuns.FirstOrDefault(r => r.PlanId == p.Id)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<BackupPlanDto> GetAsync(Guid planId, CancellationToken ct = default)
    {
        var plan = await LoadPlansQuery().FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new NotFoundException("备份计划", planId);

        var activeRun = await _db.ExecutionRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.PlanId == planId
                && ExecutionQueueService.ActiveRunStatuses.Contains(r.Status), ct);

        return Map(plan, activeRun);
    }

    public async Task<BackupPlanDto> CreateAsync(BackupPlanUpsertDto request, CancellationToken ct = default)
    {
        await EnsureNameFreeAsync(request.Name, null, ct);
        await EnsureTasksAssignableAsync(request.TaskIds, null, ct);

        var plan = new BackupPlan { Id = Guid.NewGuid() };
        ApplyFields(plan, request);

        _db.BackupPlans.Add(plan);
        plan.CreatedBy = _context.UserId;
        await _db.SaveChangesAsync(ct);

        await ReplaceItemsAsync(plan.Id, request.TaskIds, ct);

        await _audit.RecordAsync("backup_plan.create", AuditResult.Success, "backup_plan", plan.Id,
            afterData: JsonSerializer.Serialize(new { plan.Name, taskCount = request.TaskIds.Count }), ct: ct);

        _logger.LogInformation("备份计划 {Plan} 已创建，含 {Count} 个任务", plan.Name, request.TaskIds.Count);
        return await GetAsync(plan.Id, ct);
    }

    public async Task<BackupPlanDto> UpdateAsync(Guid planId, BackupPlanUpsertDto request, CancellationToken ct = default)
    {
        var plan = await _db.BackupPlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new NotFoundException("备份计划", planId);

        await EnsureNameFreeAsync(request.Name, planId, ct);
        await EnsureTasksAssignableAsync(request.TaskIds, planId, ct);

        var before = JsonSerializer.Serialize(new { plan.Name, plan.Enabled, plan.MaxConcurrent });

        ApplyFields(plan, request);
        await _db.SaveChangesAsync(ct);

        await ReplaceItemsAsync(planId, request.TaskIds, ct);

        await _audit.RecordAsync("backup_plan.update", AuditResult.Success, "backup_plan", plan.Id,
            beforeData: before,
            afterData: JsonSerializer.Serialize(new { plan.Name, taskCount = request.TaskIds.Count }), ct: ct);

        return await GetAsync(planId, ct);
    }

    public async Task DeleteAsync(Guid planId, CancellationToken ct = default)
    {
        var plan = await _db.BackupPlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new NotFoundException("备份计划", planId);

        var hasActive = await _db.ExecutionRuns
            .AnyAsync(r => r.PlanId == planId && ExecutionQueueService.ActiveRunStatuses.Contains(r.Status), ct);
        if (hasActive)
            throw new BusinessException("CONFLICT", "这个计划正在执行，先取消这次执行再删除", 409);

        // 成员任务的扫描计划要重新下发给 Agent：计划没了就该由它自己的 cron 接管
        var taskIds = await _db.BackupPlanItems.Where(i => i.PlanId == planId).Select(i => i.TaskId).ToListAsync(ct);

        _db.BackupPlans.Remove(plan);
        await _db.SaveChangesAsync(ct);
        await BumpTaskConfigVersionAsync(taskIds, ct);

        await _audit.RecordAsync("backup_plan.delete", AuditResult.Success, "backup_plan", planId,
            beforeData: JsonSerializer.Serialize(new { plan.Name }), ct: ct);

        _logger.LogInformation("备份计划 {Plan} 已删除", plan.Name);
    }

    public async Task<ExecutionRunDto> TriggerAsync(Guid planId, CancellationToken ct = default)
    {
        var run = await _queue.CreatePlanRunAsync(planId, null, "manual", _context.UserId, ct)
            ?? throw new BusinessException("CONFLICT",
                "没有产生新的执行：这个计划要么正在执行，要么里面一个任务都没有", 409);

        return await _queue.GetRunAsync(run.Id, ct);
    }

    // ---------- 内部 ----------

    private IQueryable<BackupPlan> LoadPlansQuery() =>
        _db.BackupPlans.AsNoTracking()
            .Include(p => p.Items.OrderBy(i => i.SortOrder))
                .ThenInclude(i => i.Task)
                    .ThenInclude(t => t.Client);

    private static void ApplyFields(BackupPlan plan, BackupPlanUpsertDto request)
    {
        plan.Name = request.Name.Trim();
        plan.Enabled = request.Enabled;
        plan.ScheduleKind = request.ScheduleKind == "weekly" ? PlanScheduleKind.Weekly : PlanScheduleKind.Daily;
        plan.RunAt = TimeSpan.TryParse(request.RunAt, out var runAt) ? runAt : new TimeSpan(2, 0, 0);
        plan.DaysOfWeek = plan.ScheduleKind == PlanScheduleKind.Weekly
            ? PlanSchedule.FormatDays(request.DaysOfWeek)
            : null;
        plan.Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "Asia/Shanghai" : request.Timezone.Trim();
        plan.MaxConcurrent = Math.Clamp(request.MaxConcurrent, 1, 50);
        plan.ItemTimeoutMinutes = Math.Clamp(request.ItemTimeoutMinutes, 5, 10080);
        plan.UpdatedAt = DateTime.UtcNow;
    }

    private async Task EnsureNameFreeAsync(string name, Guid? selfId, CancellationToken ct)
    {
        var trimmed = name.Trim();
        var duplicate = await _db.BackupPlans.AnyAsync(p => p.Name == trimmed && (selfId == null || p.Id != selfId), ct);
        if (duplicate)
            throw new BusinessException("DUPLICATE_PLAN_NAME", $"已存在同名计划：{trimmed}", 409);
    }

    /// <summary>
    /// 任务必须存在，且不能已经属于别的计划——一个任务被两个计划同时驱动，
    /// 「按顺序执行」这句话就不成立了（数据库上还有 uq_backup_plan_items_task 兜底）。
    /// </summary>
    private async Task EnsureTasksAssignableAsync(List<Guid> taskIds, Guid? selfPlanId, CancellationToken ct)
    {
        if (taskIds.Count == 0)
            return;

        var existing = await _db.BackupTasks.AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct);

        var missing = taskIds.Except(existing).ToList();
        if (missing.Count > 0)
            throw new NotFoundException("备份任务", string.Join(",", missing));

        var taken = await _db.BackupPlanItems.AsNoTracking()
            .Where(i => taskIds.Contains(i.TaskId) && (selfPlanId == null || i.PlanId != selfPlanId))
            .Include(i => i.Task)
            .Include(i => i.Plan)
            .ToListAsync(ct);

        if (taken.Count > 0)
            throw new BusinessException("CONFLICT",
                $"这些任务已经属于别的计划：{string.Join("；", taken.Select(i => $"{i.Task.Name} → {i.Plan.Name}"))}", 409);
    }

    /// <summary>整体替换成员：表单是「从零拼一份清单」，逐条 diff 只会把顺序改乱</summary>
    private async Task ReplaceItemsAsync(Guid planId, List<Guid> taskIds, CancellationToken ct)
    {
        var previous = await _db.BackupPlanItems.Where(i => i.PlanId == planId).ToListAsync(ct);
        _db.BackupPlanItems.RemoveRange(previous);

        var sort = 0;
        foreach (var taskId in taskIds)
        {
            _db.BackupPlanItems.Add(new BackupPlanItem
            {
                Id = Guid.NewGuid(),
                PlanId = planId,
                TaskId = taskId,
                SortOrder = sort++
            });
        }

        await _db.SaveChangesAsync(ct);

        // 进出计划都会改变「这个任务的扫描计划要不要下发给 Agent」，
        // 不递增配置版本号的话，Agent 直到下一次别的配置变更才会感知到。
        await BumpTaskConfigVersionAsync(previous.Select(i => i.TaskId).Union(taskIds).ToList(), ct);
    }

    private async Task BumpTaskConfigVersionAsync(List<Guid> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0)
            return;

        await _db.BackupTasks
            .Where(t => taskIds.Contains(t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConfigVersion, t => t.ConfigVersion + 1), ct);
    }

    private static BackupPlanDto Map(BackupPlan plan, Core.Entities.Execution.ExecutionRun? activeRun)
    {
        var tz = PlanSchedule.ResolveTimeZone(plan.Timezone);
        var days = PlanSchedule.ParseDays(plan.DaysOfWeek);

        return new BackupPlanDto
        {
            Id = plan.Id,
            Name = plan.Name,
            Enabled = plan.Enabled,
            ScheduleKind = EnumMapping.ToSnakeCase(plan.ScheduleKind),
            RunAt = $"{plan.RunAt.Hours:D2}:{plan.RunAt.Minutes:D2}",
            DaysOfWeek = days,
            Timezone = plan.Timezone,
            MaxConcurrent = plan.MaxConcurrent,
            ItemTimeoutMinutes = plan.ItemTimeoutMinutes,
            LastRunAt = plan.LastRunAt,
            NextRunAt = plan.Enabled
                ? PlanSchedule.GetNextOccurrence(
                    plan.ScheduleKind == PlanScheduleKind.Weekly, plan.RunAt, days, tz, DateTime.UtcNow)
                : null,
            CreatedAt = plan.CreatedAt,
            UpdatedAt = plan.UpdatedAt,
            Items = plan.Items.OrderBy(i => i.SortOrder).Select(i => new BackupPlanItemDto
            {
                TaskId = i.TaskId,
                TaskName = i.Task.Name,
                ApplicationName = i.Task.ApplicationName,
                ClientId = i.Task.ClientId,
                ClientName = string.IsNullOrWhiteSpace(i.Task.Client.DisplayName)
                    ? i.Task.Client.Hostname
                    : i.Task.Client.DisplayName,
                TaskEnabled = i.Task.Enabled,
                SortOrder = i.SortOrder
            }).ToList(),
            ActiveRun = activeRun is null ? null : ExecutionQueueService.Map(activeRun, withItems: false)
        };
    }
}
