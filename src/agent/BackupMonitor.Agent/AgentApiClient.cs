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

public sealed class AgentApiClient
{
    private readonly AgentOptions _options;
    private readonly AgentStateStore _stateStore;
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public AgentApiClient(IOptions<AgentOptions> options, AgentStateStore stateStore)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _handler = new HttpClientHandler();
        if (_options.AllowInsecureTls)
        {
            // 仅供本地开发联调；生产配置默认关闭。生产配置绝不能启用此分支。
            _handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }
        else if (!string.IsNullOrWhiteSpace(_options.ServerCertificateFingerprint))
        {
            var expectedFingerprint = NormalizeFingerprint(_options.ServerCertificateFingerprint);
            _handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && string.Equals(
                    NormalizeFingerprint(Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))),
                    expectedFingerprint,
                    StringComparison.OrdinalIgnoreCase);
        }

        _http = new HttpClient(_handler)
        {
            BaseAddress = new Uri(_options.ServerUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(10)
        };

        var certificate = stateStore.LoadCertificate();
        if (certificate is not null)
            _handler.ClientCertificates.Add(certificate);
    }

    public void AttachCertificate(X509Certificate2 certificate)
    {
        if (!_handler.ClientCertificates.Cast<X509Certificate2>().Any(c =>
                string.Equals(c.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
            _handler.ClientCertificates.Add(certificate);
    }

    public void ReplaceCertificate(X509Certificate2 certificate)
    {
        _handler.ClientCertificates.Clear();
        _handler.ClientCertificates.Add(certificate);
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
        Guid sessionId, Guid fileId, int chunkIndex, long offset, byte[] bytes, string sha256, CancellationToken ct)
    {
        using var content = new ByteArrayContent(bytes);
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
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
