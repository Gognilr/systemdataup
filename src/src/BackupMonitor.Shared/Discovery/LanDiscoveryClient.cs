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

        DisableUdpConnReset(socket);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var probe = SendProbesAsync(socket, bytes, ResolveBroadcastTargets(), timeoutCts.Token);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                // 外层 ct 在两次接收之间被取消时也必须停下来并抛出：停机信号不能被
                // 当成「探测窗口到了」而返回一份半截结果，理由同下面的 catch 过滤器。
                ct.ThrowIfCancellationRequested();

                UdpReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 只有「超时窗口到了」才算正常收尾。timeoutCts 是 ct 的链接源，
                    // 外层 ct 取消时它同样会 IsCancellationRequested——如果不区分这两者，
                    // 停机信号会被当成探测结束，方法返回部分结果，调用方
                    // （AgentWorker.TryRediscoverServerAddressAsync）接着拿一个已取消的
                    // ct 去 probe 候选地址。外层取消必须让异常传播出去。
                    break;
                }
                catch (SocketException ex)
                {
                    // Windows 上 UDP 套接字收到 ICMP 端口不可达，会以 WSAECONNRESET 的形式
                    // 从后续的 receive 抛出。它只说明「刚才发给某个地址的包被拒了」，
                    // 与还在路上的其他应答无关，丢掉这一次继续收。
                    // 其他 SocketError 说明套接字本身已经坏了，此时不能无条件 continue，
                    // 否则会在这里空转到超时为止。
                    if (ex.SocketErrorCode == SocketError.ConnectionReset)
                        continue;

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

                // Nonce 单独判空：属性虽然有 = string.Empty 初始化，但 System.Text.Json
                // 遇到 "nonce": null 照样会把 null 写进 setter，Encoding.GetBytes(null)
                // 抛的是 ArgumentNullException，接收循环只 catch 了 JsonException，
                // 于是一个字段填对 protocol/version 的畸形包就能打断整轮发现——
                // 安装向导和运行期重发现都会被一句「发现失败」终止。
                if (response is null
                    || response.Protocol != LanDiscoveryProtocol.Name
                    || response.ProtocolVersion != LanDiscoveryProtocol.Version
                    || string.IsNullOrEmpty(response.Nonce)
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
        }
        finally
        {
            // 任何退出路径（正常收尾、外层取消抛出、套接字失效）都要把探测任务收干净：
            // socket 马上就要随 using 释放，留一个还在往上面发包的游离任务只会在别处
            // 冒出 ObjectDisposedException。SendProbesAsync 内部吞掉了所有异常，
            // 这里的 await 不会盖掉正在传播的原始异常。
            await probe;
        }

        return results.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>SIO_UDP_CONNRESET 的控制码；Windows 专有，其他平台上不存在这个 ioctl。</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>
    /// 关掉 Windows 上「UDP 收到 ICMP 端口不可达就让后续 receive 抛 WSAECONNRESET」的行为。
    ///
    /// 广播探测天然会打到一堆没有监听这个端口的主机上，对方回 ICMP 端口不可达是正常现象。
    /// 不关掉的话，一台无关主机的拒绝就足以让整轮发现在接收阶段异常中断——
    /// 明明有服务端在应答，界面上却是「没找到服务端」。
    /// 接收循环里另有一层 ConnectionReset 兜底，是因为这个 ioctl 本身可能失败（受限套接字、
    /// 被网络过滤驱动拦截），两道都留着才不至于回到老样子。
    /// </summary>
    private static void DisableUdpConnReset(UdpClient socket)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            socket.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch (SocketException)
        {
            // 设不上就退回到接收循环里的兜底，不影响发现本身。
        }
        catch (PlatformNotSupportedException)
        {
            // 同上：这个 ioctl 在非 Windows 运行时上不存在。
        }
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
