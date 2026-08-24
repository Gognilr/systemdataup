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
}
