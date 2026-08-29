using System.Text.RegularExpressions;

namespace BackupMonitor.Shared.Recognition;

/// <summary>
/// 「这个名字看起来像不像一个日期」。
///
/// 这几个判断原本长在服务端的 StructureInference 里，Agent 引用不到。后果不是重复代码，
/// 而是**能力缺失**：Agent 根本不知道「日期目录」是什么概念，于是任何一条以此为前提的
/// 规则语义（比如「业务单元 = 下面是日期目录的那一层」）都只能在向导里演一遍，
/// 真扫的时候执行不了。
///
/// 与 RecognizerRules 下沉到 Shared 是同一个理由，且约束更硬：向导说会识别出什么，
/// 实际扫描就必须识别出什么。判定住在两个程序集里，迟早会变成两个意思。
/// </summary>
public static partial class DateName
{
    /// <summary>某一层要被判定为「日期层」，需要这个比例的目录名看起来像日期。</summary>
    public const double LayerThreshold = 0.6;

    /// <summary>
    /// 中文年月日：`2026年`、`08月`、`2026年08月`、`8月25日`、`2026年08月25日`。
    ///
    /// 靠**量词**自证语义，而不是靠放宽数字位数下限——后者会把 `ZT001`、`账套001`
    /// 一起放进来。三个分组都可选，但整串必须被完全消费，且至少命中一个：
    /// `第01车间` 没有年月日量词，一个分组都匹配不上。
    /// </summary>
    [GeneratedRegex(@"^(?:(\d{4})年)?(?:(\d{1,2})月)?(?:(\d{1,2})日)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseDateRegex();

    /// <summary>
    /// 周序号：`2026W35`、`2026-W35`、`2026_w35`、`W35`。
    /// 周数收在 1–53，`2026W54` 不成立。
    /// </summary>
    [GeneratedRegex(@"^(?:((?:19|20)\d{2})[-_]?)?[Ww](\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex WeekRegex();

    /// <summary>`2026-08` / `2026_08` 这种带分隔符的年月。不收 `202608`——那 6 位数走 YYMMDD 口径。</summary>
    [GeneratedRegex(@"^((?:19|20)\d{2})[-_](\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedYearMonthRegex();

    /// <summary>
    /// 目录名是否像一个日期或时间戳。
    ///
    /// 判据是「抽掉非数字后能不能读成一个合理的日期」，而不是穷举格式：
    /// 20260825、2026-08-25、2026_08_25、20260825_0136、财务20260825 都要认出来，
    /// 而 ZT001、账套001 这类必须认不出来（它们只有 3 位数字）。
    ///
    /// 中文年月日（`2026年08月25日`、`8月25日`、`2026年08月`）与周序号（`2026W35`）
    /// 走各自的量词/前缀判据，不经过数字位数这条口径——它们本来就自证语义。
    /// </summary>
    public static bool LooksLikeDate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // 中文年月日：有「月+日」或「年+月」就算日期；只有年份归 LooksLikeYear，
        // 只有月或只有日归 LooksLikeMonthOrDay，各管各的，不重叠。
        if (TryParseChinese(name, out var cy, out var cm, out var cd)
            && ((cm is not null && cd is not null) || (cy is not null && cm is not null)))
        {
            return true;
        }

        if (LooksLikeWeek(name) || LooksLikeYearMonth(name))
            return true;

        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (digits.Length is < 6 or > 20)
            return false;

        // 名字里字母（含中文）太多就不是日期目录了，是带编号的业务名。
        // 年月日三个量词先剥掉再计数：`2026年08月25日` 有 3 个中文字符，
        // 不剥的话它会无谓地逼近这条上限。
        if (StripChineseUnits(name).Count(char.IsLetter) > 8)
            return false;

        if (digits.Length >= 8
            && int.TryParse(digits.AsSpan(0, 4), out var year)
            && int.TryParse(digits.AsSpan(4, 2), out var month)
            && int.TryParse(digits.AsSpan(6, 2), out var day)
            && year is >= 1990 and <= 2100
            && month is >= 1 and <= 12
            && day is >= 1 and <= 31)
        {
            return true;
        }

        if (digits.Length is 6 or 7
            && int.TryParse(digits.AsSpan(0, 2), out _)
            && int.TryParse(digits.AsSpan(2, 2), out var shortMonth)
            && int.TryParse(digits.AsSpan(4, 2), out var shortDay)
            && shortMonth is >= 1 and <= 12
            && shortDay is >= 1 and <= 31)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 目录名是不是一个年份：整个名字只有 4 位数字，且落在 1990–2100；或者中文的 `2026年`。
    ///
    /// 与 LooksLikeDate 并列而不是去放宽它的 6 位数字下限——那个下限正是用来把
    /// ZT001、账套001 挡在外面的。中文量词形态是**另开的一条**判据，靠「年」这个字
    /// 自证，同样不用碰数字位数下限。
    /// </summary>
    public static bool LooksLikeYear(string? name)
    {
        if (IsAllAsciiDigits(name)
            && name!.Length == 4
            && int.TryParse(name, out var year)
            && year is >= 1990 and <= 2100)
        {
            return true;
        }

        return name is not null
            && TryParseChinese(name, out var cy, out var cm, out var cd)
            && cy is not null && cm is null && cd is null;
    }

    /// <summary>目录名是不是月或日：整个名字只有 1–2 位数字且落在 1–31，或者中文的 `08月` / `25日`。</summary>
    public static bool LooksLikeMonthOrDay(string? name)
    {
        if (IsAllAsciiDigits(name)
            && name!.Length is 1 or 2
            && int.TryParse(name, out var value)
            && value is >= 1 and <= 31)
        {
            return true;
        }

        return name is not null
            && TryParseChinese(name, out var cy, out var cm, out var cd)
            && cy is null && (cm is null) != (cd is null);
    }

    /// <summary>
    /// 目录名是不是「年月」：`2026年08月`、`2026-08`、`2026_8`。
    ///
    /// 单独一条而不是并进 LooksLikeMonthOrDay：情形零（InferNumericLayers）分层时，
    /// 「年一层、月一层」与「年月合成一层」是两种结构，混为一谈会推出错误的通配路径。
    /// 刻意不收 `202608`——那 6 位数字在既有口径里读作 YYMMDD，两种解释都成立时不猜。
    /// </summary>
    public static bool LooksLikeYearMonth(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (TryParseChinese(name, out var cy, out var cm, out var cd)
            && cy is not null && cm is not null && cd is null)
        {
            return true;
        }

        var match = SeparatedYearMonthRegex().Match(name);
        return match.Success
            && int.TryParse(match.Groups[1].Value, out var year)
            && int.TryParse(match.Groups[2].Value, out var month)
            && year is >= 1990 and <= 2100
            && month is >= 1 and <= 12;
    }

    /// <summary>目录名是不是周序号：`2026W35` / `2026-W35` / `W35`，周数 1–53。</summary>
    public static bool LooksLikeWeek(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var match = WeekRegex().Match(name);
        return match.Success
            && int.TryParse(match.Groups[2].Value, out var week)
            && week is >= 1 and <= 53;
    }

    /// <summary>
    /// 把名字解析成 DateTime，解析不出返回 null。
    ///
    /// 给 <see cref="DateSequence"/> 用：判周期与缺口要的是真实日期，不是「像不像」。
    /// 周序号按 ISO-8601 折算成该周的**周一**——一周里选哪一天都行，但必须固定，
    /// 否则相邻两周的间隔会在 1~13 天之间跳，中位数直接失去意义。
    /// </summary>
    public static DateTime? TryParse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (TryParseChinese(name, out var cy, out var cm, out var cd) && (cy ?? cm ?? cd) is not null)
        {
            // 缺哪一段就补哪一段：只有年月时按当月 1 号，只有月日时按当年。
            // 这是给「一组同构名字」算间隔用的，补法一致就不会歪。
            var year = cy ?? DateTime.UtcNow.Year;
            var month = cm ?? 1;
            var day = cd ?? 1;
            return SafeDate(year, month, day);
        }

        var week = WeekRegex().Match(name);
        if (week.Success && int.TryParse(week.Groups[2].Value, out var weekNumber) && weekNumber is >= 1 and <= 53)
        {
            var year = week.Groups[1].Success && int.TryParse(week.Groups[1].Value, out var wy)
                ? wy
                : DateTime.UtcNow.Year;
            return FirstDayOfIsoWeek(year, weekNumber);
        }

        var yearMonth = SeparatedYearMonthRegex().Match(name);
        if (yearMonth.Success
            && int.TryParse(yearMonth.Groups[1].Value, out var ymYear)
            && int.TryParse(yearMonth.Groups[2].Value, out var ymMonth))
        {
            return SafeDate(ymYear, ymMonth, 1);
        }

        var digits = new string(name.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length >= 8
            && int.TryParse(digits.AsSpan(0, 4), out var dy)
            && int.TryParse(digits.AsSpan(4, 2), out var dm)
            && int.TryParse(digits.AsSpan(6, 2), out var dd))
        {
            return SafeDate(dy, dm, dd);
        }

        if (digits.Length is 6 or 7
            && int.TryParse(digits.AsSpan(0, 2), out var sy)
            && int.TryParse(digits.AsSpan(2, 2), out var sm)
            && int.TryParse(digits.AsSpan(4, 2), out var sd))
        {
            return SafeDate(2000 + sy, sm, sd);
        }

        if (digits.Length == 4 && int.TryParse(digits, out var onlyYear) && onlyYear is >= 1990 and <= 2100)
            return SafeDate(onlyYear, 1, 1);

        return null;
    }

    /// <summary>
    /// 一组目录名里，像日期的占比是否达到 <see cref="LayerThreshold"/>。
    /// 空集合返回 false——「没有子目录」不该被算成「这一层是日期层」。
    /// </summary>
    public static bool IsDateLayer(IReadOnlyCollection<string> names) =>
        names.Count > 0 && (double)names.Count(LooksLikeDate) / names.Count >= LayerThreshold;

    // ---------- 私有 ----------

    private static bool TryParseChinese(string name, out int? year, out int? month, out int? day)
    {
        year = month = day = null;
        var match = ChineseDateRegex().Match(name);
        if (!match.Success)
            return false;

        if (match.Groups[1].Success)
        {
            if (!int.TryParse(match.Groups[1].Value, out var y) || y is < 1990 or > 2100)
                return false;
            year = y;
        }

        if (match.Groups[2].Success)
        {
            if (!int.TryParse(match.Groups[2].Value, out var m) || m is < 1 or > 12)
                return false;
            month = m;
        }

        if (match.Groups[3].Success)
        {
            if (!int.TryParse(match.Groups[3].Value, out var d) || d is < 1 or > 31)
                return false;
            day = d;
        }

        // 三个分组都可选，全空时正则会匹配空串以外的任何东西吗——不会，但空名字要挡住。
        return year is not null || month is not null || day is not null;
    }

    /// <summary>剥掉年月日三个量词，只为了「字母太多」那条计数不把量词算成业务名。</summary>
    private static string StripChineseUnits(string name) =>
        name.Replace("年", "").Replace("月", "").Replace("日", "");

    private static DateTime? SafeDate(int year, int month, int day)
    {
        if (year is < 1990 or > 2100 || month is < 1 or > 12 || day < 1)
            return null;
        if (day > DateTime.DaysInMonth(year, month))
            return null;
        return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>ISO-8601 第 N 周的周一。第 1 周是包含当年第一个星期四的那一周。</summary>
    private static DateTime? FirstDayOfIsoWeek(int year, int week)
    {
        if (year is < 1990 or > 2100)
            return null;

        var jan4 = new DateTime(year, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        var offsetToMonday = ((int)jan4.DayOfWeek + 6) % 7;
        var firstMonday = jan4.AddDays(-offsetToMonday);
        var result = firstMonday.AddDays((week - 1) * 7);
        return result.Year >= year - 1 && result.Year <= year + 1 ? result : null;
    }

    /// <summary>全角数字（０８）不算——它几乎一定不是备份程序建出来的目录。</summary>
    private static bool IsAllAsciiDigits(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(char.IsAsciiDigit);
}
