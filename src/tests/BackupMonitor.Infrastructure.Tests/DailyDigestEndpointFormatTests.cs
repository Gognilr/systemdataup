using BackupMonitor.Infrastructure.Services;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 快报正文的排版与业务探测那一块。
///
/// 这一组不碰数据库：要钉的是**这封信写了什么、长什么样**。
///
/// 排版在这里不是审美问题。这封信真正被读的地方是钉钉群，
/// 而钉钉群是在手机上看的——一行超过 20 个全角字符就折行，
/// 十条探测全绿就是十行一模一样的对勾占掉大半屏。
/// 那种信会让人养成划过去的习惯，而那个习惯迟早会把出事那天的 ✗ 一起划走。
///
/// 探测内容本身同样要钉：它写的是「不通多久了、为什么不通」，
/// 这两样写错了不会报错，只会让人照着一份指错方向的清单
/// 去重启一台根本没问题的机器。
/// </summary>
public class DailyDigestEndpointFormatTests
{
    private static readonly DailyDigestWorker.HealthThresholds Thresholds = new(85, 90, 10);

    private static DailyDigestWorker.DigestData Digest(
        IReadOnlyList<DailyDigestWorker.EndpointHealth> endpoints,
        int disabled = 0,
        int outages = 0,
        IReadOnlyList<DailyDigestWorker.ClientHealth>? clients = null) =>
        new(0, 0, 0, 0, 0, 0, 0, clients ?? [], Thresholds, endpoints, disabled, outages);

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
        DailyDigestWorker.BuildBody(new DateTime(2026, 9, 17), TimeZoneInfo.Utc, d);

    // ---------- 排版 ----------

    /// <summary>
    /// 钉钉的 text 消息不解析 Markdown：写星号粗体过去，看见的就是两个星号。
    /// 这一条钉的是「正文里不许出现 Markdown 记号」。
    /// </summary>
    [Fact]
    public void 正文里不出现Markdown记号()
    {
        var body = Render(Digest(
            [Endpoint(status: "down", consecutiveFailures: 5, error: "10 秒内没有响应")],
            outages: 2));

        Assert.DoesNotContain("**", body);
        Assert.DoesNotContain("##", body);
    }

    /// <summary>
    /// 十条全绿不能逐条列。这是这次改版的核心：
    /// 一屏一模一样的对勾天天发，人就学会了整块跳过。
    /// </summary>
    [Fact]
    public void 全部正常时探测收成一行()
    {
        var body = Render(Digest([
            Endpoint("恒源U8 登录代理", "恒源_U8服务器", "172.16.11.83:11520", latencyMs: 1),
            Endpoint("恒源U8 加密服务", "恒源_U8服务器", "172.16.11.83:4630", latencyMs: 0),
            Endpoint("瑞来OA 应用", "瑞来_OA服务器", "http://172.16.11.229:8080/", latencyMs: 70)
        ]));

        Assert.Contains("【业务探测】3 项全部正常", body);
        // 最慢的那条要留着：正常项里唯一会变化的就是这个数，变慢是塌之前最早的信号。
        Assert.Contains("最慢 70 ms：瑞来OA 应用 · 瑞来_OA服务器", body);
        // 其余两条一个字都不占——连地址都不写，钉钉会把 URL 变成蓝链再占掉两行。
        Assert.DoesNotContain("172.16.11.83", body);
    }

    /// <summary>
    /// 客户端一台两行：名字一行、数字缩进一行。
    /// 挤成一行在手机上会被折断，而折下来的那半行顶格开始，和下一台机器长得一样。
    /// </summary>
    [Fact]
    public void 客户端名字和数字分两行()
    {
        var body = Render(Digest([], clients: [
            new("恒源_OA服务器", true, 4m, 77m, 49m, @"D:\", 0)
        ]));

        Assert.Contains("【客户端】1 台全部在线", body);
        Assert.Contains("恒源_OA服务器" + Environment.NewLine + "  CPU 4%  内存 77%  D盘余 49%", body);
        // 全在线时不再每行重复「在线」——那几个字占的是手机上最贵的宽度。
        Assert.DoesNotContain("恒源_OA服务器  在线", body);
    }

    /// <summary>离线那台自己带标记，不靠表头——表头说的是总数，眼睛要能落到具体是哪台。</summary>
    [Fact]
    public void 离线的客户端单独标出来()
    {
        var body = Render(Digest([], clients: [
            new("文件服务器", false, null, null, null, null, 1),
            new("恒源_OA服务器", true, 4m, 77m, 49m, @"D:\", 0)
        ]));

        Assert.Contains("【客户端】2 台，1 台离线", body);
        Assert.Contains("文件服务器  离线 ⚠", body);
    }

    /// <summary>昨天什么都没发生也要有一句话，不能留一个空的【昨日】表头。</summary>
    [Fact]
    public void 昨日无事时只写一句()
    {
        var body = Render(Digest([Endpoint()]));

        Assert.Contains("【昨日】无备份入库，无异常", body);
    }

    // ---------- 探测内容 ----------

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
    /// last_status 永远停在最后一次的值上，满屏的正常说的是「昨天下午三点是通的」。
    /// </summary>
    [Fact]
    public void 探测很久没跑要明说状态是旧的()
    {
        var stale = DateTime.UtcNow.AddHours(-12);
        var digest = Digest([Endpoint(lastProbedAtUtc: stale, lastSuccessAtUtc: stale)]);

        var body = Render(digest);

        Assert.Contains("探测已停", body);
        // 表头绝不能同时写「全部正常」——那句话和下一行的「探测已停」自相矛盾，
        // 而读的人会信上面那句。
        Assert.DoesNotContain("全部正常", body);
        Assert.Contains("状态已旧", body);
        Assert.Contains("在最后一次探测时是正常的", body);
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
        Assert.Contains("未到 3 次门槛", body);
        // 抖动不该让一封本来干净的信变成「有事」——那正是麻木的来源。
        Assert.Contains("一切正常", DailyDigestWorker.SubjectSuffixFor(digest));
    }

    /// <summary>有异常时，正常的那些也只给一行小结，不重新铺开。</summary>
    [Fact]
    public void 有异常时正常的那些只给一行小结()
    {
        var body = Render(Digest([
            Endpoint("加密服务", "U8服务器", "10.0.0.12:4630",
                status: "down", consecutiveFailures: 5, error: "连接被拒绝"),
            Endpoint("数据库", "U8服务器", "10.0.0.12:1433", latencyMs: 8),
            Endpoint("登录代理", "U8服务器", "10.0.0.12:11520", latencyMs: 40)
        ]));

        Assert.Contains("【业务探测】3 项，1 项不通", body);
        Assert.Contains("其余 2 项正常，最慢 40 ms", body);
        // 不通的那条要写出地址；正常的两条不写。
        Assert.Contains("10.0.0.12:4630", body);
        Assert.DoesNotContain("10.0.0.12:1433", body);
    }

    /// <summary>停用的不列清单，但个数要报——否则「我明明配了探测」这个疑问没地方回答。</summary>
    [Fact]
    public void 停用的探测只报个数()
    {
        var body = Render(Digest([Endpoint()], disabled: 2));

        Assert.Contains("另有 2 项已停用", body);
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

        Assert.Contains("业务中断 2 次", body);
    }
}
