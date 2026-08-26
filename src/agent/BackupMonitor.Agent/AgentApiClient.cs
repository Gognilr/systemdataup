using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    /// <summary>保护 _http / _clientCertificate 的整体替换；请求侧只做一次快照读。</summary>
    private readonly object _clientSync = new();
    private HttpClient _http;
    private X509Certificate2? _clientCertificate;

    public AgentApiClient(IOptions<AgentOptions> options, AgentStateStore stateStore)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _http = null!;
        RebuildClient(stateStore.LoadCertificate());
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

    private void RebuildClient(X509Certificate2? certificate)
    {
        var handler = new HttpClientHandler();
        if (_options.AllowInsecureTls)
        {
            // 仅供本地开发联调；生产配置默认关闭。生产配置绝不能启用此分支。
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }
        else if (!string.IsNullOrWhiteSpace(_options.ServerCertificateFingerprint))
        {
            var expectedFingerprint = NormalizeFingerprint(_options.ServerCertificateFingerprint);
            handler.ServerCertificateCustomValidationCallback = (_, serverCertificate, _, _) =>
                serverCertificate is not null
                && string.Equals(
                    NormalizeFingerprint(Convert.ToHexString(serverCertificate.GetCertHash(HashAlgorithmName.SHA256))),
                    expectedFingerprint,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (certificate is not null)
            handler.ClientCertificates.Add(certificate);

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_options.ServerUrl.TrimEnd('/') + "/", UriKind.Absolute),
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

    public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await CurrentClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
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
