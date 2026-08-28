using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Shared.Discovery;

public sealed record DiscoveredServer(
    string InstanceId,
    string DisplayName,
    string ApiAddress,
    int ProtocolVersion);

/// <summary>
/// LAN 发现的客户端一侧。
///
/// 从安装器搬到 Shared 是因为运行期的 Agent 也要用它：服务端换 IP 之后，
/// 固化在 appsettings.json 里的地址就是死的，只有重新发现能把地址找回来
/// （方案 C）。两处各留一份实现的话，广播目标、多轮探测这些容易写错的细节
/// 迟早会在其中一份里退化。
/// </summary>
public sealed class LanDiscoveryClient
{
    // 探测发多轮：UDP 丢一个包就是丢，只发一次的话一次丢包就等于「没有服务端」。
    // 轮次有界，超时窗口内发完就只收不发，不会把广播域刷满。
    private const int ProbeRounds = 3;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken ct)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var request = new LanDiscoveryRequest { Nonce = nonce };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var results = new Dictionary<string, DiscoveredServer>(StringComparer.OrdinalIgnoreCase);

        // 显式绑定到 0.0.0.0:0：探测在后台任务里发，接收循环同时就要开始收，
        // 不能依赖「首次发送时隐式绑定」这个时序。
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0))
        {
            EnableBroadcast = true
        };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var probe = SendProbesAsync(socket, bytes, ResolveBroadcastTargets(), timeoutCts.Token);

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

            // 同一台服务端会因为多轮探测和多个广播目标重复应答，按实例 ID 覆盖即可。
            results[response.ServerInstanceId] = new DiscoveredServer(
                response.ServerInstanceId,
                string.IsNullOrWhiteSpace(response.DisplayName) ? "BackupMonitor Server" : response.DisplayName,
                response.ApiAddress.TrimEnd('/'),
                response.ProtocolVersion);
        }

        await probe;
        return results.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>
    /// 向每个广播目标重复发送探测请求。这里不抛异常：发现失败要退化成「没找到，手工填地址」，
    /// 不能因为某一块网卡发不出去就把整个自动发现打断。
    /// </summary>
    private static async Task SendProbesAsync(
        UdpClient socket,
        byte[] payload,
        IReadOnlyList<IPAddress> targets,
        CancellationToken ct)
    {
        for (var round = 0; round < ProbeRounds; round++)
        {
            foreach (var target in targets)
            {
                if (ct.IsCancellationRequested)
                    return;

                try
                {
                    await socket.SendAsync(
                        payload,
                        payload.Length,
                        new IPEndPoint(target, LanDiscoveryProtocol.DefaultPort));
                }
                catch (SocketException)
                {
                    // 某块网卡不可达（虚拟交换机、已拔线但未 Down 的网卡）不影响其余目标。
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (round == ProbeRounds - 1)
                return;

            try
            {
                await Task.Delay(ProbeInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 逐个网卡算出定向子网广播地址（如 192.168.1.255），并保留受限广播 255.255.255.255 兜底。
    /// </summary>
    /// <remarks>
    /// 只发 255.255.255.255 是不够的：受限广播只从路由表选中的那一个接口出去。
    /// 服务器上多网卡很常见（业务网 + 备份网 + Hyper-V 虚拟交换机），
    /// 包很容易从错误的网卡发出去，表现就是同一网段却「发现不了服务端」。
    /// </remarks>
    private static IReadOnlyList<IPAddress> ResolveBroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        var seen = new HashSet<string>(StringComparer.Ordinal) { IPAddress.Broadcast.ToString() };

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return targets;
        }

        foreach (var adapter in interfaces)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            IPInterfaceProperties properties;
            try
            {
                properties = adapter.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                var broadcast = TryGetDirectedBroadcast(unicast);
                if (broadcast is not null && seen.Add(broadcast.ToString()))
                    targets.Add(broadcast);
            }
        }

        return targets;
    }

    private static IPAddress? TryGetDirectedBroadcast(UnicastIPAddressInformation unicast)
    {
        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
            return null;

        IPAddress mask;
        try
        {
            mask = unicast.IPv4Mask;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }

        if (mask is null || mask.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var addressBytes = unicast.Address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4)
            return null;

        // 掩码全 0（未配置的虚拟网卡会这样报）算不出有意义的子网广播地址，
        // 按位或的结果会退化成 255.255.255.255，白白重复一次受限广播。
        if (maskBytes.All(value => value == 0))
            return null;

        var broadcastBytes = new byte[4];
        for (var i = 0; i < 4; i++)
            broadcastBytes[i] = (byte)(addressBytes[i] | (byte)~maskBytes[i]);

        return new IPAddress(broadcastBytes);
    }
}
