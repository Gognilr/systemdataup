using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>报表统计服务（第二批补充设计：只读聚合查询，不涉及数据变更）</summary>
public interface IReportService
{
    Task<ClientSummaryReportDto> GetClientSummaryAsync(CancellationToken ct = default);
    Task<BackupSummaryReportDto> GetBackupSummaryAsync(CancellationToken ct = default);
    Task<AlertSummaryReportDto> GetAlertSummaryAsync(CancellationToken ct = default);
    Task<TaskSummaryReportDto> GetTaskSummaryAsync(CancellationToken ct = default);
}

/// <summary>报表实现</summary>
public class ReportService : IReportService
{
    private static readonly AlertStatus[] ActiveStatuses =
        [AlertStatus.Open, AlertStatus.Acknowledged, AlertStatus.InProgress];

    private readonly AppDbContext _db;

    public ReportService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ClientSummaryReportDto> GetClientSummaryAsync(CancellationToken ct = default)
    {
        var grouped = await _db.Clients.AsNoTracking()
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new ClientSummaryReportDto
        {
            TotalClients = grouped.Sum(g => g.Count),
            ByStatus = grouped
                .OrderBy(g => g.Status)
                .Select(g => new EnumCountDto { Value = EnumMapping.ToSnakeCase(g.Status), Count = g.Count })
                .ToList()
        };
    }

    public async Task<BackupSummaryReportDto> GetBackupSummaryAsync(CancellationToken ct = default)
    {
        var grouped = await _db.BackupSets.AsNoTracking()
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var totals = await _db.BackupSets.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Bytes = g.Sum(s => s.TotalBytes) })
            .FirstOrDefaultAsync(ct);

        var since = DateTime.UtcNow.Date.AddDays(-13);
        var daily = await _db.BackupSets.AsNoTracking()
            .Where(s => s.UploadedAt >= since)
            .GroupBy(s => s.UploadedAt.Date)
            .Select(g => new DailyCountDto { Date = g.Key, Count = g.Count(), Bytes = g.Sum(s => s.TotalBytes) })
            .OrderBy(d => d.Date)
            .ToListAsync(ct);

        return new BackupSummaryReportDto
        {
            TotalBackupSets = totals?.Count ?? 0,
            TotalBytes = totals?.Bytes ?? 0,
            ByStatus = grouped
                .OrderBy(g => g.Status)
                .Select(g => new EnumCountDto { Value = EnumMapping.ToSnakeCase(g.Status), Count = g.Count })
                .ToList(),
            DailyUploads = daily
        };
    }

    public async Task<AlertSummaryReportDto> GetAlertSummaryAsync(CancellationToken ct = default)
    {
        var activeByLevel = await _db.Alerts.AsNoTracking()
            .Where(a => ActiveStatuses.Contains(a.Status))
            .GroupBy(a => a.Level)
            .Select(g => new { Level = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byStatus = await _db.Alerts.AsNoTracking()
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new AlertSummaryReportDto
        {
            TotalActive = activeByLevel.Sum(g => g.Count),
            ByLevel = activeByLevel
                .OrderBy(g => g.Level)
                .Select(g => new EnumCountDto { Value = EnumMapping.ToSnakeCase(g.Level), Count = g.Count })
                .ToList(),
            ByStatus = byStatus
                .OrderBy(g => g.Status)
                .Select(g => new EnumCountDto { Value = EnumMapping.ToSnakeCase(g.Status), Count = g.Count })
                .ToList()
        };
    }

    public async Task<TaskSummaryReportDto> GetTaskSummaryAsync(CancellationToken ct = default)
    {
        var totalTasks = await _db.BackupTasks.CountAsync(ct);

        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var recent = await _db.BackupSets.AsNoTracking()
            .Where(s => s.UploadedAt >= weekAgo)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Sets = g.Count(),
                Bytes = g.Sum(s => s.TotalBytes),
                Tasks = g.Select(s => s.TaskId).Distinct().Count()
            })
            .FirstOrDefaultAsync(ct);

        var lastSuccessful = await _db.BackupSets.AsNoTracking()
            .Where(s => s.Status == BackupSetStatus.Available)
            .MaxAsync(s => (DateTime?)s.UploadedAt, ct);

        return new TaskSummaryReportDto
        {
            TotalTasks = totalTasks,
            TasksWithRecentUploads = recent?.Tasks ?? 0,
            RecentUploadSets = recent?.Sets ?? 0,
            RecentUploadBytes = recent?.Bytes ?? 0,
            LastSuccessfulUploadAt = lastSuccessful
        };
    }
}
