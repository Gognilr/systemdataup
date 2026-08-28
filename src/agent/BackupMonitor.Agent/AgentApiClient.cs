using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

public sealed class AgentApiException : Exception
{
    public int StatusCode { get; }
    public string? ErrorCode { get; }

    public AgentApiException(int statusCode, string message, string? errorCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

public sealed class AgentApiClient : IDisposable
{
    private readonly AgentOptions _options;
    private readonly AgentStateStore _stateStore;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>保护 _http / _clientCertificate / _serverUrl 的整体替换；请求侧只做一次快照读。</summary>
    private readonly object _clientSync = new();
    private HttpClient _http;
    private X509Certificate2? _clientCertificate;

    /// <summary>
    /// 当前生效的服务端地址。
    ///
    /// 不直接用 _options.ServerUrl 是因为它在运行期会被地址重发现改掉（方案 C），
    /// 而 IOptions 快照是启动时那一份。state.json 里的重发现结果优先：
    /// 服务端搬过一次家之后，重启 Agent 不能又退回配置文件里那个已经失效的地址。
    /// </summary>
    private string _serverUrl;

    public AgentApiClient(IOptions<AgentOptions> options, AgentStateStore stateStore)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _http = null!;
        _serverUrl = stateStore.ServerUrlOverride ?? _options.ServerUrl;
        RebuildClient(stateStore.LoadCertificate());
    }

    /// <summary>当前生效的服务端地址（可能来自运行期重发现，未必等于 appsettings.json 里的值）。</summary>
    public string ServerUrl
    {
        get
        {
            lock (_clientSync)
                return _serverUrl;
        }
    }

    /// <summary>
    /// 首次注册获批后挂载客户端证书。
    ///
    /// 必须重建 HttpClient，不能只往 ClientCertificates 集合里加一张证书：
    /// 客户端证书是在 TLS 握手阶段协商的，握手完成后连接的身份就固定了。
    /// 注册阶段的请求（提交申请、轮询审批结果）是匿名端点，那条连接握手时
    /// Agent 手上还没有证书，于是以「不出示证书」建立并进入连接池。
    /// 之后即便把证书塞进 handler，心跳复用的仍是那条无证书的连接，
    /// 服务端 Connection.ClientCertificate 始终为 null，mTLS 端点一律 401；
    /// 而失败重试间隔（10 秒）短于连接池空闲回收时间（默认 60 秒），
    /// 这条连接永远不会被淘汰——Agent 就此永久卡死，注册成功却一次心跳都上报不了。
    /// </summary>
    public void AttachCertificate(X509Certificate2 certificate)
    {
        lock (_clientSync)
        {
            if (string.Equals(_clientCertificate?.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                return;
        }

        RebuildClient(certificate);
    }

    /// <summary>证书续签后换用新证书。同样必须重建连接池，理由见 AttachCertificate。</summary>
    public void ReplaceCertificate(X509Certificate2 certificate) => RebuildClient(certificate);

    /// <summary>
    /// 丢弃当前连接池，下一次请求重新握手，证书不变。
    /// 用于 401 之后的自愈：任何让连接身份与本地证书脱节的情况，
    /// 重新握手都能纠正，代价只是一次 TLS 握手。
    /// </summary>
    public void RecycleConnections()
    {
        X509Certificate2? certificate;
        lock (_clientSync)
        {
            certificate = _clientCertificate;
        }

        RebuildClient(certificate);
    }

    /// <summary>
    /// 卸下客户端证书，回到「匿名连接」状态（方案 B 的身份自愈）。
    ///
    /// 清本地身份之后必须连着做这一步：注册端点是匿名的，但连接池里那条连接
    /// 在握手时出示的是已经作废的旧证书，服务端仍会按旧身份看待它。
    /// 理由与 <see cref="AttachCertificate"/> 相同，只是方向相反。
    /// </summary>
    public void DetachCertificate() => RebuildClient(null);

    /// <summary>
    /// 换用新的服务端地址（方案 C 的地址自愈）。
    ///
    /// 同样要重建 HttpClient：BaseAddress 是 HttpClient 上的只读属性，
    /// 而且旧地址的连接池必须整体丢掉，否则请求还会往那台已经搬走的机器上发。
    /// </summary>
    public void ChangeServerAddress(string serverUrl)
    {
        X509Certificate2? certificate;
        lock (_clientSync)
        {
            certificate = _clientCertificate;
            _serverUrl = serverUrl;
        }

        RebuildClient(certificate);
    }

    /// <summary>
    /// 试连一个候选服务端地址，只有 TLS 指纹与本地固定值一致才算通过。
    ///
    /// 用独立的 handler 而不是当前 HttpClient：这一步的全部意义就是在切换之前
    /// 确认对面确实是原来那台服务端，绝不能因为试探而污染正在用的连接池，
    /// 更不能把客户端证书出示给一台还没验明正身的机器。
    /// 未固定指纹（ServerCertificateFingerprint 为空）时直接返回 false——
    /// 没有锚点的「验证」等于没有验证，宁可不切。
    /// </summary>
    public async Task<bool> ProbeServerAddressAsync(string serverUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ServerCertificateFingerprint))
            return false;

        var expectedFingerprint = NormalizeFingerprint(_options.ServerCertificateFingerprint);
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectPreferIPv4Async,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, serverCertificate, _, _) =>
                    MatchesFingerprint(serverCertificate, expectedFingerprint)
            }
        };
        using var probe = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = NormalizeBaseAddress(serverUrl),
            Timeout = TimeSpan.FromSeconds(15)
        };

        try
        {
            using var response = await probe.GetAsync("api/v1/agent/bootstrap", HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 指纹不符会以 TLS 失败的形式落到这里，与「连不上」一样都是「不接受」。
            return false;
        }
    }

    private void RebuildClient(X509Certificate2? certificate)
    {
        // 审计 D3：从 HttpClientHandler 换成 SocketsHttpHandler，只为拿到 ConnectCallback。
        // 服务端广播的地址是 https://{机器名}:{端口}，Windows 名称解析（LLMNR/NetBIOS）
        // 优先给回 fe80:: 链路本地地址，于是整条链路走了 IPv6，服务端看到的对端地址
        // 就是 fe80::…%12——界面上的「客户端 IP」因此永远是一串没法用的 IPv6。
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectPreferIPv4Async
        };

        string serverUrl;
        lock (_clientSync)
        {
            serverUrl = _serverUrl;
        }

        if (_options.AllowInsecureTls)
        {
            // 仅供本地开发联调；生产配置默认关闭。生产配置绝不能启用此分支。
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }
        else if (!string.IsNullOrWhiteSpace(_options.ServerCertificateFingerprint))
        {
            var expectedFingerprint = NormalizeFingerprint(_options.ServerCertificateFingerprint);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, serverCertificate, _, _) =>
                MatchesFingerprint(serverCertificate, expectedFingerprint);
        }

        if (certificate is not null)
        {
            handler.SslOptions.ClientCertificates = new X509Certificate2Collection(certificate);

            // 同时钉死选择回调：默认的挑选逻辑会按服务端下发的 acceptable issuers 过滤，
            // 而私有 CA 场景下 Kestrel 常常一个 issuer 都不下发，结果是「证书明明配了，
            // 握手时却一张都没出示」——服务端看到的是匿名连接，所有 mTLS 端点一律 401。
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }

        var http = new HttpClient(handler)
        {
            BaseAddress = NormalizeBaseAddress(serverUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };

        HttpClient? previous;
        lock (_clientSync)
        {
            previous = _http;
            _http = http;
            _clientCertificate = certificate;
        }

        // 释放旧实例才会真正关闭池中的连接。Agent 的请求全部由单一工作循环顺序发出，
        // 证书变更时不会有请求在途。
        previous?.Dispose();
    }

    private HttpClient CurrentClient()
    {
        lock (_clientSync)
        {
            return _http;
        }
    }

    public void Dispose()
    {
        HttpClient? http;
        lock (_clientSync)
        {
            http = _http;
            _http = null!;
        }

        http?.Dispose();
    }

    public Task<SubmitRegistrationResponse> SubmitRegistrationAsync(SubmitRegistrationRequest request, CancellationToken ct) =>
        SendAsync<SubmitRegistrationResponse>(HttpMethod.Post, "api/v1/agent/registrations", request, ct);

    public Task<RegistrationResultResponse> GetRegistrationResultAsync(Guid registrationId, CancellationToken ct) =>
        SendAsync<RegistrationResultResponse>(HttpMethod.Get, $"api/v1/agent/registrations/{registrationId}", null, ct);

    /// <summary>
    /// 读取服务端引导信息。匿名端点，带不带客户端证书都能调。
    ///
    /// Agent 运行期只用它取 ServerInstanceId：这一条请求走的是当前这条
    /// 已经通过指纹固定的连接，拿回来的实例 ID 才有资格当作后续地址重发现的锚点。
    /// </summary>
    public Task<AgentBootstrapResponse> GetBootstrapAsync(CancellationToken ct) =>
        SendAsync<AgentBootstrapResponse>(HttpMethod.Get, "api/v1/agent/bootstrap", null, ct);

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) =>
        SendAsync<HeartbeatResponse>(HttpMethod.Post, "api/v1/agent/heartbeat", request, ct);

    public Task<CertificateRenewalResponse> RenewCertificateAsync(CancellationToken ct) =>
        SendAsync<CertificateRenewalResponse>(HttpMethod.Post, "api/v1/agent/certificate/renew", null, ct);

    public Task<AgentConfigResponse?> GetConfigAsync(long currentVersion, CancellationToken ct) =>
        SendNullableAsync<AgentConfigResponse>(HttpMethod.Get, $"api/v1/agent/config?currentVersion={currentVersion}", null, ct);

    public Task<ClaimCommandsResponse> ClaimCommandsAsync(ClaimCommandsRequest request, CancellationToken ct) =>
        SendAsync<ClaimCommandsResponse>(HttpMethod.Post, "api/v1/agent/commands/claim", request, ct);

    public Task ReportStartedAsync(Guid commandId, CancellationToken ct) =>
        SendEmptyAsync(HttpMethod.Post, $"api/v1/agent/commands/{commandId}/started", null, ct);

    public Task ReportProgressAsync(Guid commandId, CommandProgressRequest request, CancellationToken ct) =>
        SendEmptyAsync(HttpMethod.Post, $"api/v1/agent/commands/{commandId}/progress", request, ct);

    public Task ReportCompletedAsync(Guid commandId, CommandCompletedRequest request, CancellationToken ct) =>
        SendEmptyAsync(HttpMethod.Post, $"api/v1/agent/commands/{commandId}/completed", request, ct);

    public Task<SubmitPrecheckResultResponse> SubmitPrecheckAsync(Guid taskId, SubmitPrecheckResultRequest request, CancellationToken ct) =>
        SendAsync<SubmitPrecheckResultResponse>(HttpMethod.Post, $"api/v1/agent/tasks/{taskId}/precheck-results", request, ct);

    public Task<CreateUploadSessionResponse> CreateUploadSessionAsync(CreateUploadSessionRequest request, CancellationToken ct) =>
        SendAsync<CreateUploadSessionResponse>(HttpMethod.Post, "api/v1/agent/upload-sessions", request, ct);

    public Task<MissingChunksResponse> GetMissingChunksAsync(Guid sessionId, Guid fileId, CancellationToken ct) =>
        SendAsync<MissingChunksResponse>(HttpMethod.Get, $"api/v1/agent/upload-sessions/{sessionId}/files/{fileId}/missing-chunks", null, ct);

    public async Task<UploadChunkResponse> UploadChunkAsync(
        Guid sessionId, Guid fileId, int chunkIndex, long offset, ReadOnlyMemory<byte> bytes, string sha256, CancellationToken ct)
    {
        // ReadOnlyMemoryContent 直接包装调用方切好的 length 范围，不会多发 ArrayPool.Rent
        // 可能超额分配出来的那部分垃圾字节，也不需要为了凑 byte[] 再拷贝一份。
        using var content = new ReadOnlyMemoryContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = CreateRequest(HttpMethod.Put, $"api/v1/agent/upload-sessions/{sessionId}/files/{fileId}/chunks/{chunkIndex}");
        request.Headers.Add("X-Chunk-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Chunk-Sha256", sha256);
        request.Content = content;
        return await SendRequestAsync<UploadChunkResponse>(request, ct);
    }

    public Task<CompleteUploadFileResponse> CompleteUploadFileAsync(Guid sessionId, Guid fileId, CompleteUploadFileRequest request, CancellationToken ct) =>
        SendAsync<CompleteUploadFileResponse>(HttpMethod.Post, $"api/v1/agent/upload-sessions/{sessionId}/files/{fileId}/complete", request, ct);

    public Task<CompleteUploadSessionResponse> CompleteUploadSessionAsync(Guid sessionId, CompleteUploadSessionRequest request, CancellationToken ct) =>
        SendAsync<CompleteUploadSessionResponse>(HttpMethod.Post, $"api/v1/agent/upload-sessions/{sessionId}/complete", request, ct);

    /// <summary>
    /// 取消上传会话（审计 A-10）。服务端的 cancel 接口一直存在，却从来没有客户端调用者——
    /// 于是每一次中途失败都在服务端留下一个永不结束的 uploading 会话。
    /// </summary>
    public Task CancelUploadSessionAsync(Guid sessionId, CancellationToken ct) =>
        SendEmptyAsync(HttpMethod.Post, $"api/v1/agent/upload-sessions/{sessionId}/cancel", null, ct);

    /// <summary>
    /// 中断上传会话：这次传挂了，但会话与暂存留着，下次接着传。
    /// 取消会让服务端把会话作废、暂存随后被清，下一次只能从 0 开始——
    /// 6GB 的备份传到 90% 断一次就得重来一遍，这个代价没有理由付。
    /// </summary>
    public Task InterruptUploadSessionAsync(Guid sessionId, InterruptUploadSessionRequest request, CancellationToken ct) =>
        SendEmptyAsync(HttpMethod.Post, $"api/v1/agent/upload-sessions/{sessionId}/interrupt", request, ct);

    /// <summary>
    /// 升级包下载上限（字节）。升级包整体读进 byte[] 才能算哈希，
    /// 512MB 已经远大于任何正常的 Agent 包，超过这个量级只可能是配错或被投毒。
    /// </summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    /// <summary>
    /// 下载并整体读入内存（升级包，需要先算 SHA-256 再落盘）。
    ///
    /// 审计 C-01：原先无任何大小限制，一个被改写的 packageUrl 就能让 Agent
    /// 把内存吃光。Content-Length 只是提前拒绝的快路径——它是对端说的，不能信，
    /// 因此读取过程中同样逐块累加计数，超限立刻中断。
    /// </summary>
    public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await CurrentClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength;
        if (declared > MaxDownloadBytes)
            throw new AgentApiException((int)response.StatusCode,
                $"下载内容超过上限：声明 {declared} 字节，上限 {MaxDownloadBytes} 字节", "DOWNLOAD_TOO_LARGE");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(declared is > 0 and <= MaxDownloadBytes ? (int)declared.Value : 81920);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxDownloadBytes)
                throw new AgentApiException((int)response.StatusCode,
                    $"下载内容超过上限 {MaxDownloadBytes} 字节，已中断", "DOWNLOAD_TOO_LARGE");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: _json);
        return await SendRequestAsync<T>(request, ct);
    }

    private async Task<T?> SendNullableAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: _json);
        return await SendRequestAsync<T?>(request, ct);
    }

    private async Task SendEmptyAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: _json);
        await SendRequestAsync<object?>(request, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Agent-Version", _options.AgentVersion);
        var clientId = _stateStore.ClientId;
        if (clientId is not null)
            request.Headers.Add("X-Client-Id", clientId.Value.ToString());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string NormalizeFingerprint(string value) =>
        value.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private static Uri NormalizeBaseAddress(string serverUrl) =>
        new(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);

    private static bool MatchesFingerprint(X509Certificate? serverCertificate, string expectedFingerprint) =>
        serverCertificate is not null
        && string.Equals(
            NormalizeFingerprint(Convert.ToHexString(serverCertificate.GetCertHash(HashAlgorithmName.SHA256))),
            expectedFingerprint,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>单个候选地址的连接预算；只有存在多个候选时才启用，见 ConnectPreferIPv4Async。</summary>
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 建连时优先 IPv4，连不上再回落 IPv6（审计 D3）。
    ///
    /// 默认行为是把 DNS 返回的地址按系统策略排序，而 Windows 的名称解析
    /// 对局域网内的机器名往往先给出 fe80:: 链路本地地址，于是这条链路走了 IPv6。
    /// 这里只改「先试哪一个」，不改任何配置地址：纯 IPv6 的环境下 IPv4 列表为空，
    /// 行为与改动前完全一致，仍然连得上。
    ///
    /// 逐个地址串行重试而不是 Happy Eyeballs 并发：Agent 的请求本来就不在乎
    /// 几百毫秒的建连差异，而并发连接会在服务端留下一半立刻被 RST 的半开连接。
    /// </summary>
    private static async ValueTask<Stream> ConnectPreferIPv4Async(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        // OrderBy 是稳定排序：同一族内部仍保持解析器给出的顺序，
        // 只把 IPv4 整体提到前面，不打乱系统的偏好。
        var ordered = addresses
            .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        if (ordered.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Exception? lastFailure = null;
        foreach (var address in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            // 有备选地址时给每次尝试上一个预算：被防火墙静默丢包的那一族
            // 会一直等到操作系统的连接超时（20 秒量级），回落就成了摆设。
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (ordered.Length > 1)
                attempt.CancelAfter(ConnectAttemptTimeout);

            try
            {
                await socket.ConnectAsync(address, port, attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (ct.IsCancellationRequested)
                    throw;

                // 预算耗尽在这里等价于「连不上」，换下一个地址继续。
                lastFailure = ex is OperationCanceledException
                    ? new SocketException((int)SocketError.TimedOut)
                    : ex;
            }
        }

        throw lastFailure ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private async Task<T> SendRequestAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await CurrentClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        ApiResponse<T>? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiResponse<T>>(payload, _json);
        }
        catch (JsonException)
        {
            // 继续以 HTTP 状态码报告非 JSON 错误，避免日志丢失服务器原文。
        }

        if (!response.IsSuccessStatusCode || envelope?.Success != true)
        {
            var code = envelope?.Error?.Code;
            var message = envelope?.Error?.Message ?? $"服务器返回 HTTP {(int)response.StatusCode}: {payload}";
            throw new AgentApiException((int)response.StatusCode, message, code);
        }

        return envelope.Data!;
    }
}
