namespace BackupMonitor.Shared.Recognition;

/// <summary>一组日期名的时间规律：周期，以及缺了哪几个。</summary>
public sealed record DateSequenceInfo(
    TimeSpan MedianInterval,
    string PeriodLabel,
    int ExpectedCount,
    IReadOnlyList<DateTime> MissingDates,
    DateTime Earliest,
    DateTime Latest);

/// <summary>
/// 从一组日期目录名（或文件名里的日期分组键）推出「多久备一次」和「哪几次没备」。
///
/// 放在 Shared 而不是 Infrastructure：与 DateName 下沉是同一个理由——这套语义将来
/// Agent 侧也可能要用（例如判断「这次的日期目录是不是接着上一个」）。
///
/// 三条刻意的边界：
///   一、周期取相邻间隔的**中位数**，不是平均数。一个大缺口能把平均值拉飞，
///       而缺口恰恰是这个类要找的东西——用会被缺口污染的统计量去找缺口是循环论证。
///   二、周期判不准（「不规律」）时**不产出缺口**。周期都说不准，缺口无从谈起，
///       硬给一份只会让人对着一串无中生有的日期排查。
///   三、样本 &lt; 5 不产出缺口。四个日期能算出间隔，但算不出「正常间隔是多少」。
/// </summary>
public static class DateSequence
{
    /// <summary>低于这个样本量不产出缺口——间隔算得出来，规律说不准。</summary>
    public const int MinSamplesForGaps = 5;

    /// <summary>中位间隔落在标称周期的 ±25% 内才算命中该周期。</summary>
    private const double PeriodTolerance = 0.25;

    public const string Daily = "每天";
    public const string Weekly = "每周";
    public const string Monthly = "每月";
    public const string Irregular = "不规律";

    /// <summary>
    /// 解析并分析一组日期名。解析不出日期的名字整条跳过（不猜）；
    /// 能解析出的少于 2 个时返回 null——一个点算不出间隔。
    /// </summary>
    public static DateSequenceInfo? Analyze(IEnumerable<string> dateNames)
    {
        var dates = dateNames
            .Select(DateName.TryParse)
            .Where(d => d is not null)
            .Select(d => d!.Value.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (dates.Count < 2)
            return null;

        var intervals = new List<double>(dates.Count - 1);
        for (var i = 1; i < dates.Count; i++)
            intervals.Add((dates[i] - dates[i - 1]).TotalDays);

        var medianDays = Median(intervals);
        if (medianDays <= 0)
            return null;

        var label = LabelOf(medianDays);
        var median = TimeSpan.FromDays(medianDays);
        var earliest = dates[0];
        var latest = dates[^1];

        // 不规律，或样本太少：只给周期标签，不推缺口。
        if (label == Irregular || dates.Count < MinSamplesForGaps)
            return new DateSequenceInfo(median, label, dates.Count, [], earliest, latest);

        var missing = FindMissing(dates, medianDays);
        var expected = (int)Math.Round((latest - earliest).TotalDays / medianDays) + 1;
        return new DateSequenceInfo(median, label, expected, missing, earliest, latest);
    }

    /// <summary>
    /// 按中位间隔在 [最早, 最晚] 上铺网格，找出没有对应日期的格子。
    ///
    /// 容差取半个间隔：日备的备份不会每天同一秒开始，「差一天」和「少一次」
    /// 必须分得开，否则一份正常的备份历史会被报成满是缺口。
    /// </summary>
    private static List<DateTime> FindMissing(List<DateTime> dates, double medianDays)
    {
        var tolerance = medianDays / 2;
        var missing = new List<DateTime>();
        var latest = dates[^1];

        for (var cursor = dates[0].AddDays(medianDays); cursor < latest.AddDays(-tolerance); cursor = cursor.AddDays(medianDays))
        {
            var slot = cursor;
            if (!dates.Any(d => Math.Abs((d - slot).TotalDays) <= tolerance))
                missing.Add(slot.Date);
        }

        return missing;
    }

    private static string LabelOf(double days)
    {
        if (Within(days, 1)) return Daily;
        if (Within(days, 7)) return Weekly;
        if (days is >= 28 and <= 31) return Monthly;
        return Irregular;
    }

    private static bool Within(double value, double nominal) =>
        Math.Abs(value - nominal) <= nominal * PeriodTolerance;

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
