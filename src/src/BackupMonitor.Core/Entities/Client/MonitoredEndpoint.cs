namespace BackupMonitor.Core.Entities.Client;

/// <summary>探测类型。</summary>
public enum EndpointProbeType
{
    /// <summary>连一下端口，连上就算活。</summary>
    Tcp = 0,

    /// <summary>打一个 URL，看状态码、正文和耗时。</summary>
    Http = 1
}

/// <summary>
/// 一条业务系统探测（对应 monitored_endpoints 表，V047）。
///
/// 与<see cref="MonitoredServiceDefinition"/>的分工：
/// 那个回答「进程在不在」，这个回答「业务用不用得了」。
/// 两者不能互相替代——这类系统最常见的故障形态恰恰是**进程好好的、业务已经用不了**：
/// Tomcat 活着但 webapp 已经 OOM、U8 的加密狗掉了、数据库连接池耗尽。
/// 服务状态对这些一律显示正常。
/// </summary>
public class MonitoredEndpoint
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>关联客户端；告警挂到它名下。可空——探测目标不一定装了 Agent。</summary>
    public Guid? ClientId { get; set; }

    public EndpointProbeType ProbeType { get; set; } = EndpointProbeType.Tcp;

    /// <summary>TCP：主机名或 IP。HTTP：完整 URL。</summary>
    public string Target { get; set; } = null!;

    /// <summary>TCP 才用。</summary>
    public int? Port { get; set; }

    /// <summary>HTTP 才用。逗号分隔，为空按 200 处理。登录页常会 302，所以要能填多个。</summary>
    public string? ExpectedStatus { get; set; }

    /// <summary>
    /// HTTP 才用，而且是这个功能的成败所在。
    ///
    /// 只看状态码基本没用——Tomcat 的错误页、应用的「系统维护中」页返回的都是 200。
    /// 真正区分「HTTP 通了」和「应用还活着」的，是响应里有没有那段只有正常页面才有的文本。
    /// 为空表示不校验正文。
    /// </summary>
    public string? ExpectedContent { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 连续失败几次才报。
    ///
    /// 一次抖动就报警，练几次人就麻木了——而麻木之后，连真出事的那条也会被一起划走。
    /// </summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>
    /// 响应慢于这么多毫秒也算异常（HTTP，可空表示不判）。
    /// 从 200ms 变成 8 秒是最早的预警，而那时状态码还是 200。
    /// </summary>
    public int? SlowMilliseconds { get; set; }

    public bool Enabled { get; set; } = true;

    public bool AlertOnFailure { get; set; } = true;

    public DateTime? LastProbedAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>up / down；从没探过时为空。</summary>
    public string? LastStatus { get; set; }

    public int? LastLatencyMs { get; set; }

    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Client? Client { get; set; }
}
