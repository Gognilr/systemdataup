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
public static class DateName
{
    /// <summary>某一层要被判定为「日期层」，需要这个比例的目录名看起来像日期。</summary>
    public const double LayerThreshold = 0.6;

    /// <summary>
    /// 目录名是否像一个日期或时间戳。
    ///
    /// 判据是「抽掉非数字后能不能读成一个合理的日期」，而不是穷举格式：
    /// 20260825、2026-08-25、2026_08_25、20260825_0136、财务20260825 都要认出来，
    /// 而 ZT001、账套001 这类必须认不出来（它们只有 3 位数字）。
    /// </summary>
    public static bool LooksLikeDate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (digits.Length is < 6 or > 20)
            return false;

        // 名字里字母（含中文）太多就不是日期目录了，是带编号的业务名。
        if (name.Count(char.IsLetter) > 8)
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
    /// 目录名是不是一个年份：整个名字只有 4 位数字，且落在 1990–2100。
    ///
    /// 与 LooksLikeDate 并列而不是去放宽它的 6 位数字下限——那个下限正是用来把
    /// ZT001、账套001 挡在外面的。要求「整个名字只有数字」同样是刻意的：
    /// 2026年、08月 这类一律认不出来，宁可交给人选模板，也不要认错。
    /// </summary>
    public static bool LooksLikeYear(string? name) =>
        IsAllAsciiDigits(name)
        && name!.Length == 4
        && int.TryParse(name, out var year)
        && year is >= 1990 and <= 2100;

    /// <summary>目录名是不是月或日：整个名字只有 1–2 位数字，且落在 1–31。</summary>
    public static bool LooksLikeMonthOrDay(string? name) =>
        IsAllAsciiDigits(name)
        && name!.Length is 1 or 2
        && int.TryParse(name, out var value)
        && value is >= 1 and <= 31;

    /// <summary>全角数字（０８）不算——它几乎一定不是备份程序建出来的目录。</summary>
    private static bool IsAllAsciiDigits(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(char.IsAsciiDigit);

    /// <summary>
    /// 一组目录名里，像日期的占比是否达到 <see cref="LayerThreshold"/>。
    /// 空集合返回 false——「没有子目录」不该被算成「这一层是日期层」。
    /// </summary>
    public static bool IsDateLayer(IReadOnlyCollection<string> names) =>
        names.Count > 0 && (double)names.Count(LooksLikeDate) / names.Count >= LayerThreshold;
}
