using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>管理端告警服务（设计书 20 告警接口）</summary>
public interface IAlertService
{
    Task<PagedResult<AlertListItemDto>> GetListAsync(AlertQuery query, CancellationToken ct = default);
    Task<AlertDetailDto> GetDetailAsync(Guid alertId, CancellationToken ct = default);
    Task AcknowledgeAsync(Guid alertId, CancellationToken ct = default);
    Task HandleAsync(Guid alertId, HandleAlertRequest request, CancellationToken ct = default);
    Task CloseAsync(Guid alertId, CloseAlertRequest request, CancellationToken ct = default);
}

/// <summary>告警服务实现（列表/详情/确认/处理/关闭）</summary>
public class AlertService : IAlertService
{

    // ── 列表排序白名单（审查 P1-3）
    private static readonly Dictionary<string, Func<IQueryable<Alert>, bool, IQueryable<Alert>>> AlertSorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["level"] = SortWhitelist.By<Alert, AlertLevel>(a => a.Level),
            ["status"] = SortWhitelist.By<Alert, AlertStatus>(a => a.Status),
            ["category"] = SortWhitelist.By<Alert, string?>(a => a.Category),
            ["title"] = SortWhitelist.By<Alert, string>(a => a.Title),
            ["occurrenceCount"] = SortWhitelist.By<Alert, int>(a => a.OccurrenceCount),
            ["firstOccurredAt"] = SortWhitelist.By<Alert, DateTime>(a => a.FirstOccurredAt),
            ["lastOccurredAt"] = SortWhitelist.By<Alert, DateTime>(a => a.LastOccurredAt)
        };

    private static readonly Func<IQueryable<Alert>, bool, IQueryable<Alert>> AlertSortFallback =
        SortWhitelist.By<Alert, DateTime>(a => a.LastOccurredAt);
    private readonly AppDbContext _db;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<AlertService> _logger;

    public AlertService(AppDbContext db, ICurrentContext context, IAuditRecorder audit, ILogger<AlertService> logger)
    {
        _db = db;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task<PagedResult<AlertListItemDto>> GetListAsync(AlertQuery query, CancellationToken ct = default)
    {
        var alerts = _db.Alerts.AsNoTracking()
            .Include(a => a.Client)
            .Include(a => a.Task)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Level))
        {
            if (!EnumMapping.TryParseSnakeCase<AlertLevel>(query.Level, out var level))
                throw new BusinessException("INVALID_REQUEST", $"无效的告警等级：{query.Level}", 400);
            alerts = alerts.Where(a => a.Level == level);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!EnumMapping.TryParseSnakeCase<AlertStatus>(query.Status, out var status))
                throw new BusinessException("INVALID_REQUEST", $"无效的告警状态：{query.Status}", 400);
            alerts = alerts.Where(a => a.Status == status);
        }
        else
        {
            // 默认只返回活动告警（open/acknowledged/in_progress），历史告警需显式指定状态
            alerts = alerts.Where(a =>
                a.Status == AlertStatus.Open
                || a.Status == AlertStatus.Acknowledged
                || a.Status == AlertStatus.InProgress);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
            alerts = alerts.Where(a => a.Category == query.Category);
        if (query.ClientId is not null)
            alerts = alerts.Where(a => a.ClientId == query.ClientId);
        if (query.TaskId is not null)
            alerts = alerts.Where(a => a.TaskId == query.TaskId);
        if (query.From is not null)
            alerts = alerts.Where(a => a.LastOccurredAt >= query.From);
        if (query.To is not null)
            alerts = alerts.Where(a => a.LastOccurredAt <= query.To);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
            alerts = alerts.Where(a => EF.Functions.ILike(a.Title, $"%{query.Keyword.Trim()}%"));

        var totalCount = await alerts.LongCountAsync(ct);

        var rows = await alerts
            .ApplySort(query.SortBy, query.SortDescending, AlertSorts, AlertSortFallback)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(a => new
            {
                a.Id,
                a.AlertKey,
                a.Level,
                a.Status,
                a.Category,
                a.ClientId,
                ClientHostname = a.Client != null ? a.Client.Hostname : null,
                a.TaskId,
                TaskName = a.Task != null ? a.Task.Name : null,
                a.Title,
                a.Message,
                a.FirstOccurredAt,
                a.LastOccurredAt,
                a.OccurrenceCount,
                a.AcknowledgedAt
            })
            .ToListAsync(ct);

        var items = rows.Select(a => new AlertListItemDto
        {
            Id = a.Id,
            AlertKey = a.AlertKey,
            Level = EnumMapping.ToSnakeCase(a.Level),
            Status = EnumMapping.ToSnakeCase(a.Status),
            Category = a.Category,
            ClientId = a.ClientId,
            ClientHostname = a.ClientHostname,
            TaskId = a.TaskId,
            TaskName = a.TaskName,
            Title = a.Title,
            Message = a.Message,
            FirstOccurredAt = a.FirstOccurredAt,
            LastOccurredAt = a.LastOccurredAt,
            OccurrenceCount = a.OccurrenceCount,
            AcknowledgedAt = a.AcknowledgedAt
        }).ToList();

        return PagedResult<AlertListItemDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<AlertDetailDto> GetDetailAsync(Guid alertId, CancellationToken ct = default)
    {
        var alert = await _db.Alerts.AsNoTracking()
            .Include(a => a.Client)
            .Include(a => a.Task)
            .Include(a => a.BusinessUnit)
            .Include(a => a.AcknowledgedByUser)
            .FirstOrDefaultAsync(a => a.Id == alertId, ct)
            ?? throw new NotFoundException("告警", alertId);

        return new AlertDetailDto
        {
            Id = alert.Id,
            AlertKey = alert.AlertKey,
            Level = EnumMapping.ToSnakeCase(alert.Level),
            Status = EnumMapping.ToSnakeCase(alert.Status),
            Category = alert.Category,
            ClientId = alert.ClientId,
            ClientHostname = alert.Client?.Hostname,
            TaskId = alert.TaskId,
            TaskName = alert.Task?.Name,
            Title = alert.Title,
            Message = alert.Message,
            FirstOccurredAt = alert.FirstOccurredAt,
            LastOccurredAt = alert.LastOccurredAt,
            OccurrenceCount = alert.OccurrenceCount,
            AcknowledgedAt = alert.AcknowledgedAt,

            BusinessUnitId = alert.BusinessUnitId,
            BusinessUnitName = alert.BusinessUnit?.DisplayName,
            BackupSetId = alert.BackupSetId,
            AcknowledgedBy = alert.AcknowledgedBy,
            AcknowledgedByName = alert.AcknowledgedByUser?.DisplayName,
            RecoveredAt = alert.RecoveredAt,
            ClosedAt = alert.ClosedAt,
            HandlingNote = alert.HandlingNote,
            Metadata = alert.Metadata
        };
    }

    /// <summary>确认告警（设计书 20.2）</summary>
    public async Task AcknowledgeAsync(Guid alertId, CancellationToken ct = default)
    {
        var alert = await _db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct)
            ?? throw new NotFoundException("告警", alertId);

        if (alert.Status != AlertStatus.Open)
            throw new BusinessException("CONFLICT",
                $"告警当前状态为 {EnumMapping.ToSnakeCase(alert.Status)}，只有未处理告警可以确认", 409);

        alert.Status = AlertStatus.Acknowledged;
        alert.AcknowledgedBy = _context.UserId;
        alert.AcknowledgedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("alert.acknowledge", AuditResult.Success, "alert", alert.Id, ct: ct);
    }

    /// <summary>更新处理状态（设计书 20.3：处理中 / 忽略）</summary>
    public async Task HandleAsync(Guid alertId, HandleAlertRequest request, CancellationToken ct = default)
    {
        if (!EnumMapping.TryParseSnakeCase<AlertStatus>(request.Status, out var target)
            || target is not (AlertStatus.InProgress or AlertStatus.Ignored))
            throw new BusinessException("INVALID_REQUEST", "处理状态只能是 in_progress 或 ignored", 400);

        var alert = await _db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct)
            ?? throw new NotFoundException("告警", alertId);

        if (alert.Status is not (AlertStatus.Open or AlertStatus.Acknowledged or AlertStatus.InProgress))
            throw new BusinessException("CONFLICT",
                $"告警当前状态为 {EnumMapping.ToSnakeCase(alert.Status)}，不允许更新处理状态", 409);

        if (alert.Status == AlertStatus.Open)
        {
            // 未经确认直接处理时补充确认信息
            alert.AcknowledgedBy ??= _context.UserId;
            alert.AcknowledgedAt ??= DateTime.UtcNow;
        }

        alert.Status = target;
        if (!string.IsNullOrWhiteSpace(request.Note))
            alert.HandlingNote = request.Note;
        if (target == AlertStatus.Ignored)
            alert.ClosedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("alert.handle", AuditResult.Success, "alert", alert.Id,
            afterData: $"{{\"status\":\"{request.Status}\"}}", ct: ct);
    }

    /// <summary>关闭告警（设计书 20.4）</summary>
    public async Task CloseAsync(Guid alertId, CloseAlertRequest request, CancellationToken ct = default)
    {
        var alert = await _db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct)
            ?? throw new NotFoundException("告警", alertId);

        if (alert.Status is AlertStatus.Closed or AlertStatus.Ignored)
            throw new BusinessException("CONFLICT",
                $"告警当前状态为 {EnumMapping.ToSnakeCase(alert.Status)}，不能重复关闭", 409);

        alert.Status = AlertStatus.Closed;
        alert.ClosedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Note))
            alert.HandlingNote = request.Note;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("alert.close", AuditResult.Success, "alert", alert.Id, ct: ct);
    }
}
