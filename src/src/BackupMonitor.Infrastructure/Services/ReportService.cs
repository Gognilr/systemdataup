using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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

    private static readonly UploadStatus[] ActiveUploadStatuses =
        [UploadStatus.Created, UploadStatus.WaitingPermission, UploadStatus.Uploading, UploadStatus.Paused, UploadStatus.RetryWait, UploadStatus.Verifying];

    private readonly AppDbContext _db;
    private readonly IUploadStorage _storage;
    private readonly IConfiguration _configuration;

    public ReportService(AppDbContext db, IUploadStorage storage, IConfiguration configuration)
    {
        _db = db;
        _storage = storage;
        _configuration = configuration;
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

        var reportTimezone = ResolveTimeZone(_configuration["Reports:Timezone"]);
        var reportToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, reportTimezone).Date;
        var since = reportToday.AddDays(-13);
        var until = reportToday.AddDays(1);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(since, DateTimeKind.Unspecified), reportTimezone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(until, DateTimeKind.Unspecified), reportTimezone);
        var dailyEvents = await _db.BackupSets.AsNoTracking()
            .Where(s => s.UploadedAt >= startUtc && s.UploadedAt < endUtc)
            .Select(s => new { s.UploadedAt, s.TotalBytes })
            .ToListAsync(ct);
        var daily = dailyEvents
            .GroupBy(s => TimeZoneInfo.ConvertTimeFromUtc(s.UploadedAt.ToUniversalTime(), reportTimezone).Date)
            .Select(g => new DailyCountDto { Date = g.Key, Count = g.Count(), Bytes = g.Sum(s => s.TotalBytes) })
            .OrderBy(d => d.Date)
            .ToList();

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

        var now = DateTime.UtcNow;
        var reportTimezone = ResolveTimeZone(_configuration["Reports:Timezone"]);
        var today = TimeZoneInfo.ConvertTimeFromUtc(now, reportTimezone).Date;
        var sinceDate = today.AddDays(-13);
        var untilDate = today.AddDays(1);
        var weekAgo = now.AddDays(-7);
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

        var taskSources = await _db.BackupTasks.AsNoTracking()
            .Select(t => new
            {
                t.Id,
                TaskName = t.Name,
                ClientHostname = t.Client.Hostname,
                t.Enabled,
                t.ImportanceLevel,
                t.TaskMode,
                t.ScanSchedule,
                t.ScheduleTimezone,
                t.LastScanAt,
                t.LastPrecheckStatus
            })
            .ToListAsync(ct);

        // UploadedAt/StartedAt 均以 UTC 存储；先取窗口原始事件，再按每个任务的
        // ScheduleTimezone 转换到该任务的本地日，不能直接在数据库按 UTC 日期分组。
        var queryWindowStart = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(sinceDate.AddDays(-2), DateTimeKind.Unspecified), reportTimezone);
        var queryWindowEnd = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(untilDate.AddDays(2), DateTimeKind.Unspecified), reportTimezone);
        var backupEvents = await _db.BackupSets.AsNoTracking()
            .Where(s => s.UploadedAt >= queryWindowStart && s.UploadedAt < queryWindowEnd)
            .Select(s => new { s.TaskId, s.UploadedAt, s.Status, s.TotalBytes })
            .ToListAsync(ct);

        var activeSessions = await _db.UploadSessions.AsNoTracking()
            .Where(s => ActiveUploadStatuses.Contains(s.Status)
                        && (s.StartedAt ?? s.CreatedAt) >= queryWindowStart
                        && (s.StartedAt ?? s.CreatedAt) < queryWindowEnd)
            .Select(s => new { s.TaskId, At = s.StartedAt ?? s.CreatedAt })
            .ToListAsync(ct);

        var taskTimezones = taskSources.ToDictionary(
            task => task.Id,
            task => ResolveTimeZone(task.ScheduleTimezone));
        var backupByDay = new Dictionary<(Guid TaskId, DateTime Date), DayAggregate>();
        foreach (var backup in backupEvents)
        {
            if (!taskTimezones.TryGetValue(backup.TaskId, out var taskTimezone))
                continue;

            var taskLocalDate = TimeZoneInfo.ConvertTimeFromUtc(backup.UploadedAt.ToUniversalTime(), taskTimezone).Date;
            var reportDate = MapTaskDateToReportDate(taskLocalDate, taskTimezone, reportTimezone);
            if (reportDate < sinceDate || reportDate >= untilDate)
                continue;

            var aggregate = backupByDay.TryGetValue((backup.TaskId, reportDate), out var existing)
                ? existing
                : new DayAggregate();
            aggregate.Count++;
            aggregate.Bytes += backup.TotalBytes;
            if (backup.Status == BackupSetStatus.Available)
                aggregate.AvailableCount++;
            else if (backup.Status == BackupSetStatus.Verifying)
                aggregate.VerifyingCount++;
            else
                aggregate.FailedCount++;
            backupByDay[(backup.TaskId, reportDate)] = aggregate;
        }

        var activeByDay = new Dictionary<(Guid TaskId, DateTime Date), int>();
        foreach (var session in activeSessions)
        {
            if (!taskTimezones.TryGetValue(session.TaskId, out var taskTimezone))
                continue;

            var taskLocalDate = TimeZoneInfo.ConvertTimeFromUtc(session.At.ToUniversalTime(), taskTimezone).Date;
            var reportDate = MapTaskDateToReportDate(taskLocalDate, taskTimezone, reportTimezone);
            if (reportDate < sinceDate || reportDate >= untilDate)
                continue;
            activeByDay[(session.TaskId, reportDate)] = activeByDay.GetValueOrDefault((session.TaskId, reportDate)) + 1;
        }
        var dates = Enumerable.Range(0, 14).Select(offset => sinceDate.AddDays(offset)).ToList();

        var matrix = taskSources
            .Select(task =>
            {
                var days = dates.Select(date =>
                {
                    backupByDay.TryGetValue((task.Id, date), out var backup);
                    activeByDay.TryGetValue((task.Id, date), out var activeCount);
                    var taskTimezone = taskTimezones[task.Id];
                    var taskDate = MapReportDateToTaskDate(date, reportTimezone, taskTimezone);
                    var taskToday = TimeZoneInfo.ConvertTimeFromUtc(now, taskTimezone).Date;
                    var status = ResolveDayStatus(
                        task.Enabled,
                        task.TaskMode,
                        task.ScanSchedule,
                        task.LastScanAt,
                        task.LastPrecheckStatus,
                        taskDate,
                        taskToday,
                        taskTimezone,
                        backup?.AvailableCount > 0,
                        backup?.VerifyingCount > 0,
                        backup?.FailedCount > 0,
                        activeCount > 0);

                    return new TaskDailyStatusDto
                    {
                        Date = date,
                        Status = status,
                        BackupSetCount = backup?.Count ?? 0,
                        BackupBytes = backup?.Bytes ?? 0
                    };
                }).ToList();

                return new TaskMatrixRowDto
                {
                    TaskId = task.Id,
                    TaskName = task.TaskName,
                    ClientHostname = task.ClientHostname,
                    Enabled = task.Enabled,
                    ImportanceLevel = EnumMapping.ToSnakeCase(task.ImportanceLevel),
                    Days = days
                };
            })
            .OrderByDescending(row => row.Days.Count(day => day.Status == "failed"))
            .ThenByDescending(row => row.Days.Count(day => day.Status == "in_progress"))
            .ThenByDescending(row => ImportanceRank(row.ImportanceLevel))
            .ThenBy(row => row.TaskName)
            .ToList();

        var repositoryBytes = await _db.BackupSets.AsNoTracking()
            .Where(s => s.Status == BackupSetStatus.Available)
            .Select(s => (long?)s.TotalBytes)
            .SumAsync(ct) ?? 0;
        var (diskFreeBytes, diskTotalBytes) = await GetDiskCapacityAsync(ct);
        var observedDailyBytes = backupByDay.Values
            .Where(day => day.AvailableCount > 0)
            .Sum(day => day.Bytes) / 14d;
        DateTime? estimatedFullAt = null;
        if (diskFreeBytes > 0 && observedDailyBytes > 0)
        {
            var daysToFull = diskFreeBytes / observedDailyBytes;
            if (daysToFull <= (DateTime.MaxValue - now).TotalDays)
                estimatedFullAt = now.AddDays(daysToFull);
        }

        return new TaskSummaryReportDto
        {
            TotalTasks = totalTasks,
            TasksWithRecentUploads = recent?.Tasks ?? 0,
            RecentUploadSets = recent?.Sets ?? 0,
            RecentUploadBytes = recent?.Bytes ?? 0,
            LastSuccessfulUploadAt = lastSuccessful,
            Dates = dates,
            Matrix = matrix,
            Capacity = new RepositoryCapacityDto
            {
                RepositoryBytes = repositoryBytes,
                DiskFreeBytes = diskFreeBytes,
                DiskTotalBytes = diskTotalBytes,
                EstimatedFullAt = estimatedFullAt
            }
        };
    }

    private async Task<(long FreeBytes, long TotalBytes)> GetDiskCapacityAsync(CancellationToken ct)
    {
        var repositoryRoot = await _storage.GetRepositoryRootAsync(ct);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(repositoryRoot));
            if (string.IsNullOrWhiteSpace(root))
                return (0, 0);

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    private sealed class DayAggregate
    {
        public int Count { get; set; }
        public long Bytes { get; set; }
        public int AvailableCount { get; set; }
        public int VerifyingCount { get; set; }
        public int FailedCount { get; set; }
    }

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
            // Windows hosts may expose the same zones under Windows IDs.
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

    private static DateTime MapTaskDateToReportDate(
        DateTime taskLocalDate,
        TimeZoneInfo taskTimezone,
        TimeZoneInfo reportTimezone)
    {
        var unspecified = DateTime.SpecifyKind(taskLocalDate.Date, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, taskTimezone);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, reportTimezone).Date;
    }

    private static DateTime MapReportDateToTaskDate(
        DateTime reportDate,
        TimeZoneInfo reportTimezone,
        TimeZoneInfo taskTimezone)
    {
        var unspecified = DateTime.SpecifyKind(reportDate.Date, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, reportTimezone);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, taskTimezone).Date;
    }

    private static string ResolveDayStatus(
        bool enabled,
        TaskMode taskMode,
        string? scanSchedule,
        DateTime? lastScanAt,
        PrecheckStatus? lastPrecheckStatus,
        DateTime date,
        DateTime today,
        TimeZoneInfo taskTimezone,
        bool hasAvailableBackup,
        bool hasVerifyingBackup,
        bool hasFailedBackup,
        bool hasActiveUpload)
    {
        if (hasAvailableBackup)
            return "success";
        if (hasVerifyingBackup || hasActiveUpload)
            return "in_progress";
        if (hasFailedBackup || IsFailedScan(lastScanAt, lastPrecheckStatus, date, taskTimezone))
            return "failed";
        if (!IsScheduledOnDate(enabled, taskMode, scanSchedule, date))
            return "no_schedule";
        return date >= today ? "in_progress" : "failed";
    }

    private static bool IsFailedScan(
        DateTime? lastScanAt,
        PrecheckStatus? status,
        DateTime date,
        TimeZoneInfo taskTimezone)
    {
        if (lastScanAt is null || status is null)
            return false;

        var scanDate = TimeZoneInfo.ConvertTimeFromUtc(lastScanAt.Value.ToUniversalTime(), taskTimezone).Date;
        if (scanDate != date.Date)
            return false;

        return status.Value is PrecheckStatus.Failed
            or PrecheckStatus.RequiredFileMissing
            or PrecheckStatus.SizeAbnormal
            or PrecheckStatus.PathNotFound
            or PrecheckStatus.AccessDenied;
    }

    private static bool IsScheduledOnDate(bool enabled, TaskMode taskMode, string? schedule, DateTime date)
    {
        if (!enabled || taskMode is TaskMode.Manual or TaskMode.MonitorOnly or TaskMode.Paused)
            return false;
        if (string.IsNullOrWhiteSpace(schedule))
            return false;

        var fields = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 5)
            return true;
        if (fields.Length > 5)
            fields = fields[^5..];

        if (!CronFieldMatches(fields[3], date.Month, 1, 12))
            return false;

        var dayOfMonthMatches = CronFieldMatches(fields[2], date.Day, 1, 31);
        var dayOfWeek = (int)date.DayOfWeek;
        var dayOfWeekMatches = CronFieldMatches(fields[4], dayOfWeek, 0, 7)
            || (dayOfWeek == 0 && CronFieldMatches(fields[4], 7, 0, 7));
        var dayOfMonthWildcard = IsCronWildcard(fields[2]);
        var dayOfWeekWildcard = IsCronWildcard(fields[4]);

        return dayOfMonthWildcard && dayOfWeekWildcard
            || dayOfMonthWildcard && dayOfWeekMatches
            || dayOfWeekWildcard && dayOfMonthMatches
            || !dayOfMonthWildcard && !dayOfWeekWildcard && (dayOfMonthMatches || dayOfWeekMatches);
    }

    private static bool IsCronWildcard(string value) => value.Trim() is "*" or "?";

    private static bool CronFieldMatches(string expression, int value, int min, int max)
    {
        foreach (var rawPart in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var stepParts = rawPart.Split('/', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(stepParts.Length == 2 ? stepParts[1] : "1", out var step) || step <= 0)
                continue;

            var range = stepParts[0];
            var rangeParts = range.Split('-', 2, StringSplitOptions.TrimEntries);
            var start = range == "*" || range == "?" ? min : ParseCronValue(rangeParts[0], min, max);
            var end = range == "*" || range == "?"
                ? max
                : rangeParts.Length == 2 ? ParseCronValue(rangeParts[1], min, max) : start;
            if (start < min || end > max || start > end)
                continue;
            for (var current = start; current <= end; current += step)
            {
                if (current == value)
                    return true;
            }
        }

        return false;
    }

    private static int ParseCronValue(string value, int min, int max) =>
        int.TryParse(value, out var parsed) ? parsed : min - 1;

    private static int ImportanceRank(string value) => value switch
    {
        "critical" => 4,
        "high" => 3,
        "normal" => 2,
        "low" => 1,
        _ => 0
    };
}
