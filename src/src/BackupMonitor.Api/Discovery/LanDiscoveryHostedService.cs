using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Security;

namespace BackupMonitor.Api.Discovery;

/// <summary>
/// LAN-only UDP discovery responder. It only returns public location metadata；
/// enrollment and key bootstrap use the advertised HTTPS endpoint。
/// </summary>
public sealed class LanDiscoveryHostedService : BackgroundService
{
    private const int MaxResponsesPerSecondPerSource = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IConfiguration _configuration;
    private readonly ILogger<LanDiscoveryHostedService> _logger;
    private readonly ConcurrentDictionary<string, RateWindow> _rateWindows = new(StringComparer.Ordinal);

    public LanDiscoveryHostedService(
        IConfiguration configuration,
        ILogger<LanDiscoveryHostedService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("LanMode:DiscoveryEnabled", false))
        {
            _logger.LogInformation("LAN discovery disabled");
            return;
        }

        var port = Math.Clamp(
            _configuration.GetValue("LanMode:DiscoveryPort", LanDiscoveryProtocol.DefaultPort),
            1024,
            65535);

        try
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            _logger.LogInformation("LAN discovery listening on UDP {Port}", port);
            while (!stoppingToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(stoppingToken);
                if (!LanNetworkPolicy.IsPrivateOrLoopback(received.RemoteEndPoint.Address)
                    || !AllowResponse(received.RemoteEndPoint.Address))
                    continue;

                // UDP 发送不应阻塞接收循环；失败由后台观察任务记录，避免未观察异常终止宿主。
                _ = RespondAndLogAsync(socket, received, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止。
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "LAN discovery unavailable on UDP {Port}; API remains available", port);
        }
    }

    private async Task RespondAndLogAsync(
        UdpClient socket,
        UdpReceiveResult received,
        CancellationToken ct)
    {
        try
        {
            await RespondAsync(socket, received, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 服务停止时取消发送属于正常路径。
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // 接收循环结束后 socket 已释放，忽略正在退出的发送任务。
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "LAN discovery response to {RemoteEndpoint} failed", received.RemoteEndPoint);
        }
    }

    private bool AllowResponse(IPAddress address)
    {
        var now = DateTime.UtcNow;
        if (_rateWindows.Count > 2048)
        {
            foreach (var item in _rateWindows)
            {
                lock (item.Value)
                {
                    if (now - item.Value.WindowStartUtc > TimeSpan.FromMinutes(1))
                        _rateWindows.TryRemove(item.Key, out _);
                }
            }
        }

        var key = address.ToString();
        var window = _rateWindows.GetOrAdd(key, _ => new RateWindow(now));
        lock (window)
        {
            if (now - window.WindowStartUtc >= TimeSpan.FromSeconds(1))
            {
                window.WindowStartUtc = now;
                window.Count = 0;
            }

            window.Count++;
            return window.Count <= MaxResponsesPerSecondPerSource;
        }
    }

    private async Task RespondAsync(UdpClient socket, UdpReceiveResult received, CancellationToken ct)
    {
        if (!LanNetworkPolicy.IsPrivateOrLoopback(received.RemoteEndPoint.Address))
            return;

        LanDiscoveryRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<LanDiscoveryRequest>(received.Buffer, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (request is null
            || !string.Equals(request.Protocol, LanDiscoveryProtocol.Name, StringComparison.Ordinal)
            || request.ProtocolVersion != LanDiscoveryProtocol.Version
            || string.IsNullOrWhiteSpace(request.Nonce)
            || request.Nonce.Length is < 16 or > 128)
            return;

        var response = new LanDiscoveryResponse
        {
            Nonce = request.Nonce,
            ServerInstanceId = _configuration["Server:InstanceId"] ?? string.Empty,
            DisplayName = _configuration["Server:DisplayName"]
                ?? _configuration["LanMode:DisplayName"]
                ?? "BackupMonitor Server",
            ApiAddress = ResolveApiAddress()
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        await socket.SendAsync(bytes, bytes.Length, received.RemoteEndPoint);
    }

    private string ResolveApiAddress()
    {
        var configured = _configuration["LanMode:AdvertisedUrl"]
            ?? _configuration["Server:AdvertisedUrl"];
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.ToString().TrimEnd('/');

        var port = 5080;
        var endpoint = _configuration["Kestrel:Endpoints:Https:Url"]
            ?? _configuration["Kestrel:Endpoints:Http:Url"];
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            port = endpointUri.Port;

        var scheme = string.Equals(
                _configuration["DeploymentMode"],
                "LanSimple",
                StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";
        return $"{scheme}://{Environment.MachineName}:{port}";
    }

    private sealed class RateWindow
    {
        public RateWindow(DateTime windowStartUtc)
        {
            WindowStartUtc = windowStartUtc;
        }

        public DateTime WindowStartUtc { get; set; }
        public int Count { get; set; }
    }
}
