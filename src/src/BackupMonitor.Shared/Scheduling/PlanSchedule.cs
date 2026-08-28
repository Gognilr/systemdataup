namespace BackupMonitor.Shared.Scheduling;

/// <summary>
/// 备份计划的时刻计算（V028）。计划不用 cron：界面上只让人选「每天 / 每周几」和一个时刻，
/// 因此这里也只算这两种，不引入 cron 的全部语义。
///
/// 全部输入输出都是 UTC，本地时刻只在计划自己的时区里出现。
/// </summary>
public static class PlanSchedule
{
    /// <summary>
    /// 距 <paramref name="nowUtc"/> 最近的一次「已到点」的执行时刻（UTC）。
    /// 找不到（例如每周只选了某天而最近 8 天内都没有）时返回 null。
    /// </summary>
    public static DateTime? GetPreviousOccurrence(
        bool weekly, TimeSpan runAt, IReadOnlyCollection<int> daysOfWeek, TimeZoneInfo tz, DateTime nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);

        // 往回找 8 天足够覆盖「每周只选一天」；再往回就是补跑窗口该管的事了。
        for (var back = 0; back <= 8; back++)
        {
            var day = localNow.Date.AddDays(-back);
            if (!MatchesDay(weekly, daysOfWeek, day))
                continue;

            var localDue = day + runAt;
            if (localDue > localNow)
                continue;

            return ToUtc(localDue, tz);
        }

        return null;
    }

    /// <summary>下一次执行时刻（UTC）。停用的计划由调用方决定不显示。</summary>
    public static DateTime? GetNextOccurrence(
        bool weekly, TimeSpan runAt, IReadOnlyCollection<int> daysOfWeek, TimeZoneInfo tz, DateTime nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);

        for (var ahead = 0; ahead <= 8; ahead++)
        {
            var day = localNow.Date.AddDays(ahead);
            if (!MatchesDay(weekly, daysOfWeek, day))
                continue;

            var localDue = day + runAt;
            if (localDue <= localNow)
                continue;

            return ToUtc(localDue, tz);
        }

        return null;
    }

    /// <summary>ISO 周几：1=周一 … 7=周日（DayOfWeek 里周日是 0）</summary>
    public static int IsoDayOfWeek(DateTime local) =>
        local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;

    private static bool MatchesDay(bool weekly, IReadOnlyCollection<int> daysOfWeek, DateTime localDay) =>
        !weekly || daysOfWeek.Contains(IsoDayOfWeek(localDay));

    /// <summary>
    /// 本地时刻 → UTC。夏令时里「不存在的时刻」（春季跳过的那一小时）不能直接转换，
    /// 会抛 ArgumentException；这里顺延到该时刻之后第一个存在的时刻，
    /// 而不是让整个计划因为一年里的一天而不执行。
    /// </summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var candidate = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 4 && tz.IsInvalidTime(candidate); i++)
            candidate = candidate.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
    }

    /// <summary>
    /// 解析时区 ID。服务端存 IANA ID（默认 Asia/Shanghai），部分 Windows 主机只认 Windows ID，
    /// 保持与 MissedBackupWorker / ReportService 相同的回退链；最终解析不了退回 UTC。
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
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

    /// <summary>"1,3,5" → [1,3,5]；空/脏数据返回空集合</summary>
    public static List<int> ParseDays(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => int.TryParse(p, out var v) ? v : 0)
                .Where(v => v is >= 1 and <= 7)
                .Distinct()
                .OrderBy(v => v)
                .ToList();

    /// <summary>[3,1] → "1,3"；空集合返回 null</summary>
    public static string? FormatDays(IEnumerable<int> days)
    {
        var ordered = days.Where(d => d is >= 1 and <= 7).Distinct().OrderBy(d => d).ToList();
        return ordered.Count == 0 ? null : string.Join(',', ordered);
    }
}
