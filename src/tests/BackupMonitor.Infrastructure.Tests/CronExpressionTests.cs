using BackupMonitor.Shared.Scheduling;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 扫描计划的 cron 由 Agent 本地解析执行，算错一次的后果是「到点没有扫描」——
/// 而这件事没有任何即时反馈，往往要等到需要恢复数据时才发现。
/// 因此边界行为必须钉死，尤其是「日」「周」两段并存时的并集语义。
/// </summary>
public sealed class CronExpressionTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private static DateTime NextLocal(string expression, DateTime afterLocal, TimeZoneInfo? zone = null)
    {
        var timeZone = zone ?? Shanghai;
        var cron = Parse(expression);
        var afterUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(afterLocal, DateTimeKind.Unspecified), timeZone);
        var next = cron.GetNextOccurrence(afterUtc, timeZone);
        Assert.NotNull(next);
        return TimeZoneInfo.ConvertTimeFromUtc(next.Value, timeZone);
    }

    private static DateTime PrevLocal(string expression, DateTime beforeLocal, TimeZoneInfo? zone = null)
    {
        var timeZone = zone ?? Shanghai;
        var cron = Parse(expression);
        var beforeUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(beforeLocal, DateTimeKind.Unspecified), timeZone);
        var previous = cron.GetPreviousOccurrence(beforeUtc, timeZone);
        Assert.NotNull(previous);
        return TimeZoneInfo.ConvertTimeFromUtc(previous.Value, timeZone);
    }

    private static CronExpression Parse(string expression)
    {
        Assert.True(CronExpression.TryParse(expression, out var cron, out var error), error);
        return cron!;
    }

    [Fact]
    public void 每天凌晨两点()
    {
        Assert.Equal(
            new DateTime(2026, 8, 24, 2, 0, 0),
            NextLocal("0 2 * * *", new DateTime(2026, 8, 23, 14, 30, 0)));
    }

    /// <summary>刚好落在触发时刻时必须给出「下一次」，否则同一分钟内会被反复触发。</summary>
    [Fact]
    public void 触发时刻本身不算下一次()
    {
        Assert.Equal(
            new DateTime(2026, 8, 25, 2, 0, 0),
            NextLocal("0 2 * * *", new DateTime(2026, 8, 24, 2, 0, 0)));
    }

    [Theory]
    [InlineData("*/15 * * * *", "2026-08-24 10:07:00", "2026-08-24 10:15:00")]
    [InlineData("0 */6 * * *", "2026-08-24 07:00:00", "2026-08-24 12:00:00")]
    [InlineData("30 1,13 * * *", "2026-08-24 02:00:00", "2026-08-24 13:30:00")]
    [InlineData("0 3 1 * *", "2026-08-24 00:00:00", "2026-09-01 03:00:00")]
    [InlineData("0 4 * * MON", "2026-08-24 05:00:00", "2026-08-31 04:00:00")]
    [InlineData("0 0 1 JAN *", "2026-08-24 00:00:00", "2027-01-01 00:00:00")]
    public void 常用写法(string expression, string after, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), NextLocal(expression, DateTime.Parse(after)));
    }

    /// <summary>星期 7 与 0 都表示周日。2026-08-24 是周一，最近的周日是 08-30。</summary>
    [Theory]
    [InlineData("0 5 * * 0")]
    [InlineData("0 5 * * 7")]
    [InlineData("0 5 * * SUN")]
    public void 周日的三种写法等价(string expression)
    {
        Assert.Equal(
            new DateTime(2026, 8, 30, 5, 0, 0),
            NextLocal(expression, new DateTime(2026, 8, 24, 0, 0, 0)));
    }

    /// <summary>
    /// Vixie cron 的历史行为：日和周两段都被限定时取并集。
    /// 2026-09-01 是周二，表达式「每月 1 号」与「每个周五」的并集里，
    /// 从 8-31（周一）之后最先到来的是 9-1。按交集实现的话会跳到某个「1 号又恰好是周五」的日子。
    /// </summary>
    [Fact]
    public void 日与周同时限定时取并集()
    {
        Assert.Equal(
            new DateTime(2026, 9, 1, 0, 0, 0),
            NextLocal("0 0 1 * FRI", new DateTime(2026, 8, 31, 12, 0, 0)));

        // 紧接着应当是最近的周五 9-4，而不是下个月 1 号。
        Assert.Equal(
            new DateTime(2026, 9, 4, 0, 0, 0),
            NextLocal("0 0 1 * FRI", new DateTime(2026, 9, 1, 0, 0, 0)));
    }

    /// <summary>只限定一段时不能变成并集，否则「每月 1 号」会退化成「每天」。</summary>
    [Fact]
    public void 只限定日时不与周取并集()
    {
        Assert.Equal(
            new DateTime(2026, 9, 1, 0, 0, 0),
            NextLocal("0 0 1 * *", new DateTime(2026, 8, 2, 0, 0, 0)));
    }

    [Fact]
    public void 时区按任务配置而非本机()
    {
        var utc = TimeZoneInfo.Utc;
        var cron = Parse("0 2 * * *");

        // 上海时间 8-24 02:00 等于 UTC 8-23 18:00；同一个表达式在两个时区给出不同的绝对时刻。
        var fromUtcBase = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 8, 23, 18, 0, 0, DateTimeKind.Utc), cron.GetNextOccurrence(fromUtcBase, Shanghai));
        Assert.Equal(new DateTime(2026, 8, 24, 2, 0, 0, DateTimeKind.Utc), cron.GetNextOccurrence(fromUtcBase, utc));
    }

    /// <summary>2 月 30 日永远不会到来，必须返回 null 而不是死循环或抛异常。</summary>
    [Fact]
    public void 永不发生的日期返回空()
    {
        Assert.Null(Parse("0 0 30 2 *").GetNextOccurrence(DateTime.UtcNow, Shanghai));
    }

    [Fact]
    public void 闰日只在闰年触发()
    {
        Assert.Equal(
            new DateTime(2028, 2, 29, 0, 0, 0),
            NextLocal("0 0 29 2 *", new DateTime(2026, 8, 24, 0, 0, 0)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0 2 * *")]
    [InlineData("0 2 * * * *")]
    [InlineData("60 2 * * *")]
    [InlineData("0 24 * * *")]
    [InlineData("0 2 0 * *")]
    [InlineData("0 2 * 13 *")]
    [InlineData("0 2 * * 8")]
    [InlineData("*/0 2 * * *")]
    [InlineData("每天两点")]
    [InlineData("@daily")]
    [InlineData("0 10-2 * * *")]
    public void 非法表达式被拒绝(string? expression)
    {
        Assert.False(CronExpression.TryParse(expression, out var cron, out var error));
        Assert.Null(cron);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.NotNull(CronExpression.Validate(expression));
    }

    [Theory]
    [InlineData("0 2 * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 0 1 * MON")]
    [InlineData("  0   2  *  *  *  ")]
    [InlineData("0 2 ? * ?")]
    public void 合法表达式通过校验(string expression)
    {
        Assert.Null(CronExpression.Validate(expression));
    }

    // ---------------------------------------------------------------------
    // GetPreviousOccurrence（整改 A2）：漏备份判定的基准是「上一次本该在什么时候跑」。
    // 算早了就误报，算晚了就漏报，而两种错误都没有即时反馈。
    // ---------------------------------------------------------------------

    /// <summary>
    /// 往返一致性：连取两次「下一次」得到 n1、n2，则 n2 之前的「上一次」必须正好是 n1。
    ///
    /// 刻意不写成 GetPrevious(GetNext(t)) == t —— 那只在 t 本身恰好是触发时刻时成立，
    /// 用任意起点会得到一个看似合理其实错误的断言。
    /// </summary>
    [Theory]
    [InlineData("0 2 * * *")]      // 每天
    [InlineData("0 4 * * MON")]    // 每周
    [InlineData("0 3 1 * *")]      // 每月
    [InlineData("0 * * * *")]      // 每小时
    public void 上一次与下一次互为逆运算(string expression)
    {
        var cron = Parse(expression);
        var start = new DateTime(2026, 8, 24, 13, 47, 0, DateTimeKind.Utc);

        var first = cron.GetNextOccurrence(start, Shanghai);
        Assert.NotNull(first);
        var second = cron.GetNextOccurrence(first!.Value, Shanghai);
        Assert.NotNull(second);

        Assert.Equal(first, cron.GetPreviousOccurrence(second!.Value, Shanghai));
    }

    /// <summary>触发时刻本身不算「上一次」，否则漏备份判定会拿当前这一刻当基准，永远差一轮。</summary>
    [Fact]
    public void 触发时刻本身不算上一次()
    {
        Assert.Equal(
            new DateTime(2026, 8, 23, 2, 0, 0),
            PrevLocal("0 2 * * *", new DateTime(2026, 8, 24, 2, 0, 0)));
    }

    [Theory]
    [InlineData("*/15 * * * *", "2026-08-24 10:07:00", "2026-08-24 10:00:00")]
    [InlineData("0 */6 * * *", "2026-08-24 07:00:00", "2026-08-24 06:00:00")]
    [InlineData("30 1,13 * * *", "2026-08-24 02:00:00", "2026-08-24 01:30:00")]
    [InlineData("0 4 * * MON", "2026-08-24 05:00:00", "2026-08-24 04:00:00")]
    [InlineData("0 0 1 JAN *", "2026-08-24 00:00:00", "2026-01-01 00:00:00")]
    public void 常用写法的上一次(string expression, string before, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), PrevLocal(expression, DateTime.Parse(before)));
    }

    /// <summary>
    /// 跨月边界：整天不匹配时必须整天跳过而不是逐分钟回退。
    /// 9-01 03:00 之前的「每月 1 号 3 点」是 8-01，中间隔着 31 天。
    /// </summary>
    [Fact]
    public void 跨月边界()
    {
        Assert.Equal(
            new DateTime(2026, 8, 1, 3, 0, 0),
            PrevLocal("0 3 1 * *", new DateTime(2026, 9, 1, 3, 0, 0)));

        // 跨年同理：1-01 之前的上一次是去年 12-01。
        Assert.Equal(
            new DateTime(2025, 12, 1, 3, 0, 0),
            PrevLocal("0 3 1 * *", new DateTime(2026, 1, 1, 2, 59, 0)));
    }

    /// <summary>月末天数不同的月份：3-31 之后回看「每月 31 号」应落到 1-31，跳过没有 31 号的 2 月。</summary>
    [Fact]
    public void 跳过没有该日期的月份()
    {
        Assert.Equal(
            new DateTime(2026, 1, 31, 0, 0, 0),
            PrevLocal("0 0 31 * *", new DateTime(2026, 3, 31, 0, 0, 0)));
    }

    /// <summary>
    /// 夏令时向前拨掉的那一小时在本地不存在，回看时必须跳过而不是抛异常。
    /// 纽约 2026-03-08 02:00 直接跳到 03:00，所以「每天 02:30」在那天没有发生，
    /// 3-09 之前的上一次是 3-07 02:30。
    /// </summary>
    [Fact]
    public void 夏令时前拨日不存在的时刻被跳过()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal(
            new DateTime(2026, 3, 7, 2, 30, 0),
            PrevLocal("30 2 * * *", new DateTime(2026, 3, 9, 0, 0, 0), newYork));
    }

    /// <summary>
    /// 夏令时后拨日的重复时刻是「歧义时间」，TimeZoneInfo 按标准时间偏移解析。
    /// 这里断言的是转换的确定性（转回本地仍是 01:30），不断言它落在 EDT 还是 EST 的哪一遍。
    /// </summary>
    [Fact]
    public void 夏令时后拨日的歧义时刻按标准时间解析()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal(
            new DateTime(2026, 11, 1, 1, 30, 0),
            PrevLocal("30 1 * * *", new DateTime(2026, 11, 2, 0, 0, 0), newYork));
    }

    /// <summary>时区按任务配置而非本机，反向查找同样如此。</summary>
    [Fact]
    public void 上一次也按任务时区()
    {
        var cron = Parse("0 2 * * *");
        var from = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        // 上海 8-24 02:00 = UTC 8-23 18:00；UTC 时区下的上一次则是 8-24 02:00Z。
        Assert.Equal(new DateTime(2026, 8, 23, 18, 0, 0, DateTimeKind.Utc), cron.GetPreviousOccurrence(from, Shanghai));
        Assert.Equal(new DateTime(2026, 8, 24, 2, 0, 0, DateTimeKind.Utc), cron.GetPreviousOccurrence(from, TimeZoneInfo.Utc));
    }

    /// <summary>永不发生的日期反向查找同样返回 null，而不是死循环或抛异常。</summary>
    [Fact]
    public void 永不发生的日期反向查找返回空()
    {
        Assert.Null(Parse("0 0 30 2 *").GetPreviousOccurrence(DateTime.UtcNow, Shanghai));
    }
}
