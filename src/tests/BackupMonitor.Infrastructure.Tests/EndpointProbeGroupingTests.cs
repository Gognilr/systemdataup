using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Infrastructure.Services;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 业务探测的告警合并与维护静默。
///
/// 两件事错了都不会报错，只会静默地多发或少发：
/// 合并判错 → 一台机器宕了群里刷三条，或者反过来一条都不报；
/// 告警键的前缀不对 → 维护模式那条 client:{id}:% 的静默罩不住探测告警，
/// 人点了「进入维护」，半夜照样被业务探测叫醒。
/// </summary>
public class EndpointProbeGroupingTests
{
    /// <summary>
    /// 合并告警的键必须挂在 client:{id}: 前缀下。
    ///
    /// 这一条看着像在测一个字符串拼接，实际测的是「维护模式管不管用」：
    /// 维护下发的静默模式是 client:{id}:%，前缀对不上就一个字都拦不住。
    /// </summary>
    [Fact]
    public void 合并告警的键能被维护静默的模式匹配到()
    {
        var clientId = Guid.NewGuid();

        var key = EndpointProbeWorker.GroupAlertKeyOf(clientId);
        var pattern = ClientAdminService.MaintenancePatternOf(clientId);

        Assert.Equal($"client:{clientId}:endpoints", key);
        // SQL LIKE 的 % 在这里等价于前缀匹配
        Assert.StartsWith(pattern.TrimEnd('%'), key);
    }

    /// <summary>没到阈值不算要报——一次抖动就报警，练几次人就麻木了。</summary>
    [Fact]
    public void 连续失败没到阈值时不算要报()
    {
        Assert.False(EndpointProbeWorker.IsAlerting(
            New(failures: 2, threshold: 3)));
        Assert.True(EndpointProbeWorker.IsAlerting(
            New(failures: 3, threshold: 3)));
    }

    /// <summary>关掉了「失败时产生告警」的探测，失败多少次都不报。</summary>
    [Fact]
    public void 关掉告警的探测不算要报()
    {
        Assert.False(EndpointProbeWorker.IsAlerting(
            New(failures: 99, threshold: 3, alertOnFailure: false)));
    }

    /// <summary>
    /// 合并正文里，好的坏的都要列出来。
    ///
    /// 正常的那几项不是废话——它们说明机器和网络是通的，坏的是上面跑的那套业务。
    /// 这个区分决定了人接下来是去重启服务还是去查网络，而「2/3 不通」和「3/3 不通」
    /// 指向的是完全不同的两种故障。
    /// </summary>
    [Fact]
    public void 合并正文里正常的项也要列出来()
    {
        var message = EndpointProbeWorker.BuildGroupMessage(
        [
            New(name: "瑞来U8 加密服务", target: "172.16.11.141", port: 4630,
                failures: 3, threshold: 3, error: "连接被拒绝"),
            New(name: "瑞来U8 数据库", target: "172.16.11.141", port: 1433,
                failures: 0, threshold: 3, latency: 89)
        ]);

        Assert.Contains("✗ 瑞来U8 加密服务  172.16.11.141:4630 — 连接被拒绝", message);
        Assert.Contains("✓ 瑞来U8 数据库  172.16.11.141:1433 — 正常（89 ms）", message);
    }

    /// <summary>HTTP 探测的目标是完整 URL，不该再拼一个端口上去。</summary>
    [Fact]
    public void HTTP探测的目标不拼端口()
    {
        var endpoint = New(name: "恒源OA 应用", target: "http://172.16.11.175:8088/seeyon/index.jsp");
        endpoint.ProbeType = EndpointProbeType.Http;

        Assert.Equal("http://172.16.11.175:8088/seeyon/index.jsp",
            EndpointProbeWorker.DescribeTarget(endpoint));
    }

    private static MonitoredEndpoint New(
        string name = "探测",
        string target = "172.16.11.1",
        int? port = 1433,
        int failures = 0,
        int threshold = 3,
        bool alertOnFailure = true,
        string? error = null,
        int? latency = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Target = target,
        Port = port,
        ProbeType = EndpointProbeType.Tcp,
        ConsecutiveFailures = failures,
        FailureThreshold = threshold,
        AlertOnFailure = alertOnFailure,
        LastError = error,
        LastLatencyMs = latency
    };
}
