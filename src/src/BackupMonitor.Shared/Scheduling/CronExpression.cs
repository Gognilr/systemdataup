namespace BackupMonitor.Shared.Scheduling;

/// <summary>
/// 五段 cron 表达式（分 时 日 月 周），Agent 侧自调度扫描用。
///
/// 放在 Shared 而不是 Agent 里，是为了让服务端保存任务时用同一份实现校验：
/// cron 写错了如果要等到「约定的时间没有扫描」才发现，排查成本极高，
/// 而且从界面上完全看不出错在哪一段。
///
/// 语义按 Vixie cron：
/// - 支持 * ? a a-b */n a-b/n a/n 以及逗号列表；
/// - 月份和星期接受英文缩写（JAN..DEC / SUN..SAT）；星期 0 和 7 都表示周日；
/// - 「日」和「周」两段都被限定时取并集（这是 cron 的历史行为，
///   0 0 1 * MON 表示「每月 1 号，以及每个周一」，不是两者同时满足）。
/// </summary>
public sealed class CronExpression
{
    private const int SearchYears = 4;

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronExpression(
        bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek,
        bool dayOfMonthRestricted, bool dayOfWeekRestricted)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    public static bool TryParse(string? expression, out CronExpression? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "表达式为空";
            return false;
        }

        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"需要 5 段（分 时 日 月 周），实际 {fields.Length} 段";
            return false;
        }

        if (!TryParseField(fields[0], 0, 59, null, "分", out var minutes, out _, out error)
            || !TryParseField(fields[1], 0, 23, null, "时", out var hours, out _, out error)
            || !TryParseField(fields[2], 1, 31, null, "日", out var daysOfMonth, out var domRestricted, out error)
            || !TryParseField(fields[3], 1, 12, MonthNames, "月", out var months, out _, out error)
            || !TryParseField(fields[4], 0, 7, DayNames, "周", out var daysOfWeekRaw, out var dowRestricted, out error))
        {
            return false;
        }

        // 星期允许写 7 表示周日，折叠回 0。
        var daysOfWeek = new bool[7];
        for (var index = 0; index <= 7; index++)
        {
            if (daysOfWeekRaw[index])
                daysOfWeek[index % 7] = true;
        }

        parsed = new CronExpression(
            minutes, hours, daysOfMonth, months, daysOfWeek, domRestricted, dowRestricted);
        return true;
    }

    /// <summary>校验用：表达式非法时返回中文原因，合法时返回 null。</summary>
    public static string? Validate(string? expression) =>
        TryParse(expression, out _, out var error) ? null : error;

    /// <summary>
    /// 求 afterUtc 之后的下一次触发时刻（UTC）。永远不会返回等于 afterUtc 的时刻。
    /// 匹配在 timeZone 的本地时间上进行——运维说的「凌晨两点」指的是机器所在时区的两点。
    /// 表达式描述的日期永不出现时（例如 2 月 30 日）返回 null。
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime afterUtc, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), timeZone);
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);
        var limit = candidate.AddYears(SearchYears);

        while (candidate < limit)
        {
            if (!MatchesDate(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            // 夏令时向前拨掉的那一小时在本地并不存在，跳过；否则 ConvertTimeToUtc 会抛异常。
            if (timeZone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone);
        }

        return null;
    }

    private bool MatchesDate(DateTime local)
    {
        if (!_months[local.Month])
            return false;

        var byDayOfMonth = _daysOfMonth[local.Day];
        var byDayOfWeek = _daysOfWeek[(int)local.DayOfWeek];

        // 两段都被限定时取并集；只限定一段时另一段全通，与运算即可。
        return _dayOfMonthRestricted && _dayOfWeekRestricted
            ? byDayOfMonth || byDayOfWeek
            : byDayOfMonth && byDayOfWeek;
    }

    private static bool TryParseField(
        string field, int min, int max, string[]? names, string label,
        out bool[] allowed, out bool restricted, out string? error)
    {
        allowed = new bool[max + 1];
        restricted = field != "*" && field != "?";
        error = null;

        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0)
                {
                    error = $"「{label}」段的步长无效：{part}";
                    return false;
                }
            }

            int from, to;
            if (range == "*" || range == "?")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = range.IndexOf('-');
                if (dash > 0)
                {
                    if (!TryParseValue(range[..dash], min, max, names, out from)
                        || !TryParseValue(range[(dash + 1)..], min, max, names, out to))
                    {
                        error = $"「{label}」段的区间无效：{part}";
                        return false;
                    }
                }
                else
                {
                    if (!TryParseValue(range, min, max, names, out from))
                    {
                        error = $"「{label}」段的取值无效：{part}";
                        return false;
                    }

                    // 单值带步长（a/n）按「从 a 起到上界」处理；不带步长就是单值。
                    to = slash >= 0 ? max : from;
                }
            }

            if (from > to)
            {
                error = $"「{label}」段的区间起点大于终点：{part}";
                return false;
            }

            for (var value = from; value <= to; value += step)
                allowed[value] = true;
        }

        if (Array.IndexOf(allowed, true) < 0)
        {
            error = $"「{label}」段没有匹配到任何取值：{field}";
            return false;
        }

        return true;
    }

    private static bool TryParseValue(string text, int min, int max, string[]? names, out int value)
    {
        text = text.Trim();
        if (int.TryParse(text, out value))
            return value >= min && value <= max;

        if (names is not null)
        {
            // 月份名从 1 开始，星期名从 0 开始；用 min 还原偏移。
            var index = Array.FindIndex(names, name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                value = index + min;
                return true;
            }
        }

        return false;
    }
}
