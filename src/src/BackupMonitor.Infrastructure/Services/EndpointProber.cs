using System.Diagnostics;
using System.Net.Sockets;
using BackupMonitor.Core.Entities.Client;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>一次探测的结果。</summary>
public sealed record EndpointProbeResult(
    bool Success,
    int LatencyMs,
    string? Error,
    int? StatusCode = null,
    string? BodyPreview = null);

/// <summary>
/// 业务系统探测。
///
/// 单拎成一个类是为了能单测：判定逻辑（哪些状态码算通过、正文校验、慢算不算失败）
/// 错了不会报错，只会静默地一直绿或者一直红——而一条永远绿的探测比没有探测更糟，
/// 因为它会让人以为这一块有人看着。
/// </summary>
public interface IEndpointProber
{
    Task<EndpointProbeResult> ProbeAsync(
        MonitoredEndpoint endpoint, bool capturePreview, CancellationToken ct);
}

public sealed class EndpointProber : IEndpointProber
{
    /// <summary>「立即试一次」回显的正文长度。够挑关键词，又不至于把一整页塞进响应里。</summary>
    public const int PreviewLength = 600;

    /// <summary>
    /// 探测用的 HttpClient。
    ///
    /// 三个刻意的设置：
    /// - **不跟随重定向**：配了期望 302 的人要的就是 302 本身，跟随之后拿到的是别处的 200，
    ///   那条期望就永远对不上了。
    /// - **不校验证书**：这是存活探测，不是安全边界。内网自签证书很常见，
    ///   为了证书让探测报红，只会让人把整条探测关掉。
    /// - **不带凭据**：探的是登录页这类匿名可达的东西。
    /// </summary>
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    });

    public async Task<EndpointProbeResult> ProbeAsync(
        MonitoredEndpoint endpoint, bool capturePreview, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(endpoint.TimeoutSeconds, 1, 120));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            return endpoint.ProbeType == EndpointProbeType.Http
                ? await ProbeHttpAsync(endpoint, capturePreview, stopwatch, timeoutCts.Token)
                : await ProbeTcpAsync(endpoint, stopwatch, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EndpointProbeResult(
                false, (int)stopwatch.ElapsedMilliseconds, $"{timeout.TotalSeconds:F0} 秒内没有响应");
        }
        catch (Exception ex)
        {
            return new EndpointProbeResult(false, (int)stopwatch.ElapsedMilliseconds, Shorten(ex.Message));
        }
    }

    private static async Task<EndpointProbeResult> ProbeTcpAsync(
        MonitoredEndpoint endpoint, Stopwatch stopwatch, CancellationToken ct)
    {
        if (endpoint.Port is not (> 0 and <= 65535))
            return new EndpointProbeResult(false, 0, "端口没有配置或不合法");

        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Target.Trim(), endpoint.Port.Value, ct);
        return new EndpointProbeResult(true, (int)stopwatch.ElapsedMilliseconds, null);
    }

    private static async Task<EndpointProbeResult> ProbeHttpAsync(
        MonitoredEndpoint endpoint, bool capturePreview, Stopwatch stopwatch, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint.Target.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new EndpointProbeResult(false, 0, "地址不是合法的 http/https URL");
        }

        // 只有需要看正文时才读正文：一条每分钟跑一次的探测，没必要每次把整页拉下来。
        var needsBody = capturePreview || !string.IsNullOrWhiteSpace(endpoint.ExpectedContent);
        var completion = needsBody
            ? HttpCompletionOption.ResponseContentRead
            : HttpCompletionOption.ResponseHeadersRead;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await Http.SendAsync(request, completion, ct);

        var status = (int)response.StatusCode;
        var body = needsBody ? await response.Content.ReadAsStringAsync(ct) : null;
        var latency = (int)stopwatch.ElapsedMilliseconds;
        var preview = capturePreview ? Truncate(body, PreviewLength) : null;

        if (!StatusAllowed(status, endpoint.ExpectedStatus))
        {
            return new EndpointProbeResult(
                false, latency,
                $"状态码 {status}，期望 {Describe(endpoint.ExpectedStatus)}", status, preview);
        }

        if (!string.IsNullOrWhiteSpace(endpoint.ExpectedContent)
            && (body is null || !body.Contains(endpoint.ExpectedContent.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            // 这一支是整个功能的意义所在：状态码 200 但正文不对，说明 HTTP 层活着、
            // 应用层已经死了（Tomcat 错误页、"系统维护中"页返回的都是 200）。
            return new EndpointProbeResult(
                false, latency,
                $"状态码 {status} 正常，但响应里找不到「{Shorten(endpoint.ExpectedContent, 40)}」",
                status, preview);
        }

        if (endpoint.SlowMilliseconds is > 0 && latency >= endpoint.SlowMilliseconds)
        {
            return new EndpointProbeResult(
                false, latency,
                $"响应耗时 {latency} 毫秒，超过 {endpoint.SlowMilliseconds} 毫秒",
                status, preview);
        }

        return new EndpointProbeResult(true, latency, null, status, preview);
    }

    /// <summary>期望状态码。为空按 200 处理；支持逗号分隔（登录页常会 302）。</summary>
    internal static bool StatusAllowed(int status, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return status == 200;

        foreach (var part in expected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var code) && code == status)
                return true;

        return false;
    }

    private static string Describe(string? expected) =>
        string.IsNullOrWhiteSpace(expected) ? "200" : expected.Trim();

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max] + "…";

    /// <summary>异常信息可能很长（整段内层异常链），截到一行能看完。</summary>
    internal static string Shorten(string? message, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "失败";

        var single = message.ReplaceLineEndings(" ").Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

}
