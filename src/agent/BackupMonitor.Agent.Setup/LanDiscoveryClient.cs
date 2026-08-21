using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Agent.Setup;

internal sealed record DiscoveredServer(
    string InstanceId,
    string DisplayName,
    string ApiAddress,
    int ProtocolVersion);

internal sealed class LanDiscoveryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken ct)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var request = new LanDiscoveryRequest { Nonce = nonce };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var results = new Dictionary<string, DiscoveredServer>(StringComparer.OrdinalIgnoreCase);

        using var socket = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        await socket.SendAsync(
            bytes,
            bytes.Length,
            new IPEndPoint(IPAddress.Broadcast, LanDiscoveryProtocol.DefaultPort));

        while (!timeoutCts.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                break;
            }

            LanDiscoveryResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<LanDiscoveryResponse>(received.Buffer, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (response is null
                || response.Protocol != LanDiscoveryProtocol.Name
                || response.ProtocolVersion != LanDiscoveryProtocol.Version
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(response.Nonce),
                    System.Text.Encoding.UTF8.GetBytes(nonce))
                || !Uri.TryCreate(response.ApiAddress, UriKind.Absolute, out var api)
                || api.Scheme != Uri.UriSchemeHttp && api.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrWhiteSpace(response.ServerInstanceId))
                continue;

            results[response.ServerInstanceId] = new DiscoveredServer(
                response.ServerInstanceId,
                string.IsNullOrWhiteSpace(response.DisplayName) ? "BackupMonitor Server" : response.DisplayName,
                response.ApiAddress.TrimEnd('/'),
                response.ProtocolVersion);
        }

        return results.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
