using BackupMonitor.Shared.Recognition;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// B4：从一组日期名推「多久备一次」和「哪几次没备」。
/// B5：日期形态补齐中文年月日与周序号。
///
/// 两件事放在一起测，因为 DateSequence 的解析能力完全来自 DateName——
/// 周序号能不能认出来、`2026年08月` 折成哪一天，直接决定周期算得对不对。
/// </summary>
public class DateSequenceTests
{
    // ---------- B5：日期形态 ----------

    [Theory]
    [InlineData("2026年", true)]
    [InlineData("1989年", false)]      // 年份下限外
    [InlineData("2101年", false)]
    [InlineData("2026", true)]
    [InlineData("ZT001", false)]
    [InlineData("账套001", false)]
    [InlineData("第01车间", false)]
    public void 年份形态(string name, bool expected) =>
        Assert.Equal(expected, DateName.LooksLikeYear(name));

    [Theory]
    [InlineData("08月", true)]
    [InlineData("25日", true)]
    [InlineData("08", true)]
    [InlineData("13月", false)]        // 月份越界
    [InlineData("０８", false)]         // 全角数字继续不认
    [InlineData("账套001", false)]
    public void 月日形态(string name, bool expected) =>
        Assert.Equal(expected, DateName.LooksLikeMonthOrDay(name));

    [Theory]
    [InlineData("2026年08月", true)]
    [InlineData("2026-08", true)]
    [InlineData("2026_8", true)]
    [InlineData("2026年13月", false)]
    // 202608 这 6 位数字在既有口径里读作 YYMMDD，两种解释都成立时不猜。
    [InlineData("202608", false)]
    public void 年月形态(string name, bool expected) =>
        Assert.Equal(expected, DateName.LooksLikeYearMonth(name));

    [Theory]
    [InlineData("2026W35", true)]
    [InlineData("2026-W35", true)]
    [InlineData("2026_w35", true)]
    [InlineData("W35", true)]
    [InlineData("2026W54", false)]     // 周数越界
    [InlineData("W0", false)]
    public void 周序号形态(string name, bool expected) =>
        Assert.Equal(expected, DateName.LooksLikeWeek(name));

    [Theory]
    [InlineData("2026年08月25日", true)]
    [InlineData("8月25日", true)]
    [InlineData("2026年08月", true)]
    [InlineData("2026W35", true)]
    [InlineData("20260825", true)]
    [InlineData("ZT001", false)]
    [InlineData("账套001", false)]
    [InlineData("第01车间", false)]
    [InlineData("2026年13月", false)]
    public void 日期形态(string name, bool expected) =>
        Assert.Equal(expected, DateName.LooksLikeDate(name));

    // ---------- B4：周期与缺口 ----------

    [Fact]
    public void 连续二十一天判为每天且没有缺口()
    {
        var names = Enumerable.Range(0, 21)
            .Select(i => new DateTime(2026, 8, 1).AddDays(i).ToString("yyyyMMdd"))
            .ToList();

        var info = DateSequence.Analyze(names);

        Assert.NotNull(info);
        Assert.Equal(DateSequence.Daily, info!.PeriodLabel);
        Assert.Empty(info.MissingDates);
    }

    /// <summary>
    /// 抠掉三天：周期仍判「每天」（中位数不受缺口影响，这正是不用平均数的理由），
    /// 缺口精确列出这三天。
    /// </summary>
    [Fact]
    public void 抠掉三天后周期不变且缺口被精确列出()
    {
        var missing = new[] { new DateTime(2026, 8, 13), new DateTime(2026, 8, 19), new DateTime(2026, 8, 24) };
        var names = Enumerable.Range(0, 30)
            .Select(i => new DateTime(2026, 8, 1).AddDays(i))
            .Where(d => !missing.Contains(d))
            .Select(d => d.ToString("yyyyMMdd"))
            .ToList();

        var info = DateSequence.Analyze(names);

        Assert.NotNull(info);
        Assert.Equal(DateSequence.Daily, info!.PeriodLabel);
        Assert.Equal(missing, info.MissingDates);
    }

    /// <summary>样本不足不产出缺口：四个日期能算出间隔，算不出「正常间隔是多少」。</summary>
    [Fact]
    public void 样本不足时不产出缺口()
    {
        var info = DateSequence.Analyze(["20260801", "20260802", "20260804", "20260805"]);

        Assert.NotNull(info);
        Assert.Empty(info!.MissingDates);
    }

    /// <summary>间隔杂乱：判「不规律」，且此时**不算缺口**——周期都说不准，缺口无从谈起。</summary>
    [Fact]
    public void 间隔杂乱时判不规律且不产出缺口()
    {
        var days = new[] { 0, 1, 6, 8, 17, 18 };
        var names = days.Select(d => new DateTime(2026, 8, 1).AddDays(d).ToString("yyyyMMdd")).ToList();

        var info = DateSequence.Analyze(names);

        Assert.NotNull(info);
        Assert.Equal(DateSequence.Irregular, info!.PeriodLabel);
        Assert.Empty(info.MissingDates);
    }

    [Fact]
    public void 周目录判为每周()
    {
        var names = Enumerable.Range(30, 8).Select(w => $"2026W{w}").ToList();

        var info = DateSequence.Analyze(names);

        Assert.NotNull(info);
        Assert.Equal(DateSequence.Weekly, info!.PeriodLabel);
        Assert.Empty(info.MissingDates);
    }

    [Fact]
    public void 中文年月目录判为每月()
    {
        var names = Enumerable.Range(1, 8).Select(m => $"2026年{m:00}月").ToList();

        var info = DateSequence.Analyze(names);

        Assert.NotNull(info);
        Assert.Equal(DateSequence.Monthly, info!.PeriodLabel);
    }

    /// <summary>解析不出日期的名字整条跳过，不猜。全都解析不出就返回 null。</summary>
    [Fact]
    public void 解析不出日期时返回空()
    {
        Assert.Null(DateSequence.Analyze(["ZT001", "账套002", "第01车间"]));
    }
}
