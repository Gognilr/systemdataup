using BackupMonitor.Infrastructure.Services;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 快报里业务探测那一块的渲染。
///
/// 这一组不碰数据库：要钉的是**这封信写了什么**。
/// 探测部分和别处不同——它写的是「不通多久了、为什么不通」，
/// 而这两样写错了不会报错，只会让人每天读到一份看着很详细、其实指错方向的清单，
/// 然后照着它去重启一台根本没问题的机器。
/// </summary>
public class DailyDigestEndpointFormatTests
{
    private static readonly DailyDigestWorker.HealthThresholds Thresholds = new(85, 90, 10);

    private static DailyDigestWorker.DigestData Digest(
        IReadOnlyList<DailyDigestWorker.EndpointHealth> endpoints,
        int disabled = 0,
        int outages = 0) =>
        new(0, 0, 0, 0, 0, 0, 0, [], Thresholds, endpoints, disabled, outages);

    private static DailyDigestWorker.EndpointHealth Endpoint(
        string name = "U8 登录代理",
        string? clientName = "U8服务器",
        string target = "10.0.0.12:11520",
        string? status = "up",
        int? latencyMs = 12,
        int consecutiveFailures = 0,
        int failureThreshold = 3,
        int intervalSeconds = 60,
        string? error = null,
        DateTime? lastProbedAtUtc = null,
        DateTime? lastSuccessAtUtc = null) =>
        new(name, clientName, target, status, latencyMs, consecutiveFailures,
            failureThreshold, intervalSeconds, error,
            lastProbedAtUtc ?? DateTime.UtcNow.AddSeconds(-30),
            lastSuccessAtUtc ?? DateTime.UtcNow.AddSeconds(-30));

    private static string Render(DailyDigestWorker.DigestData d) =>
        DailyDigestWorker.BuildBody(new DateTime(2026, 9, 11), TimeZoneInfo.Utc, d);

    /// <summary>不通的那条要写清「是什么、在哪、多久了、为什么」——少一样人就得开电脑。</summary>
    [Fact]
    public void 不通的探测要写出原因和已经多久没通()
    {
        var body = Render(Digest([
            Endpoint(
                status: "down",
                latencyMs: 10000,
                consecutiveFailures: 7,
                error: "10 秒内没有响应",
                lastSuccessAtUtc: DateTime.UtcNow.AddHours(-3))
        ]));

        Assert.Contains("✗ U8 登录代理 · U8服务器", body);
        Assert.Contains("10.0.0.12:11520", body);
        Assert.Contains("连续失败 7 次", body);
        Assert.Contains("已 3 小时", body);
        // 原因写原文，不概括：「10 秒内没有响应」和「状态码 200 但正文不对」
        // 指向完全不同的两件事，概括掉就只剩一个「失败」。
        Assert.Contains("10 秒内没有响应", body);
        Assert.Contains("1 项不通", body);
    }

    /// <summary>
    /// 探测本身停了，是这一块里最要紧的异常，而且它长得最像「正常」：
    /// last_status 永远停在最后一次的值上，满屏 ✓ 说的是「昨天下午三点是通的」。
    /// </summary>
    [Fact]
    public void 探测很久没跑要明说状态是旧的()
    {
        var stale = DateTime.UtcNow.AddHours(-12);
        var digest = Digest([Endpoint(lastProbedAtUtc: stale, lastSuccessAtUtc: stale)]);

        var body = Render(digest);

        Assert.Contains("探测已停", body);
        Assert.Contains("状态已旧", body);
        // 标题里也要出现，否则人只看标题「一切正常」就划过去了。
        Assert.Contains("业务探测已停", DailyDigestWorker.SubjectSuffixFor(digest));
    }

    /// <summary>
    /// 还没到报警门槛的抖动照样要列出来，但不能算「不通」。
    /// 一次抖动就报，练几次人就麻木了；完全不提，昨晚开始抖的那条就没人看见——
    /// 而那通常就是塌之前的样子。
    /// </summary>
    [Fact]
    public void 没到阈值的抖动要列出来但不算不通()
    {
        var digest = Digest([
            Endpoint(status: "down", consecutiveFailures: 1, error: "状态码 502，期望 200")
        ]);

        var body = Render(digest);

        Assert.Contains("1 项在抖", body);
        Assert.DoesNotContain("项不通", body);
        Assert.Contains("还没到 3 次的报警门槛", body);
        // 抖动不该让一封本来干净的信变成「有事」——那正是麻木的来源。
        Assert.Contains("一切正常", DailyDigestWorker.SubjectSuffixFor(digest));
    }

    /// <summary>正常的那几行也要带耗时：变慢是塌之前最早的信号，而那时状态码还是 200。</summary>
    [Fact]
    public void 正常的探测带上耗时()
    {
        var body = Render(Digest([Endpoint(latencyMs: 312)]));

        Assert.Contains("✓ U8 登录代理 · U8服务器", body);
        Assert.Contains("312 ms", body);
        Assert.Contains("1 项正常", body);
    }

    /// <summary>停用的不列清单，但个数要报——否则「我明明配了探测」这个疑问没地方回答。</summary>
    [Fact]
    public void 停用的探测只报个数()
    {
        var body = Render(Digest([Endpoint()], disabled: 2));

        Assert.Contains("2 项已停用", body);
    }

    /// <summary>一条探测都没配时整块不出现——快报是看状态的，不是推销功能的。</summary>
    [Fact]
    public void 没有探测时不出现这一块()
    {
        var body = Render(Digest([]));

        Assert.DoesNotContain("业务探测", body);
        // 但日报本身照常发，尾句必须还在。
        Assert.Contains("连续收不到", body);
    }

    /// <summary>昨天中断过几次取的是告警条数，不是探测失败次数——后者一次故障能有几百。</summary>
    [Fact]
    public void 昨日业务中断按次数写()
    {
        var body = Render(Digest([Endpoint()], outages: 2));

        Assert.Contains("昨日业务中断：2 次", body);
    }
}
