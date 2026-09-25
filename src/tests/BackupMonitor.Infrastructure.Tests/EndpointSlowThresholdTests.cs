using System.Net;
using System.Net.Sockets;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Infrastructure.Services;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 「慢于 N 毫秒也算异常」这条判据。
///
/// 它原先只在 HTTP 探测里生效，TCP 测了连接耗时却不拿来判定。
/// 补上 TCP 是因为有些业务根本不说 HTTP——Java RMI 这类私有协议上，
/// 除了「连不连得上」之外，连接耗时是唯一还能看出点东西的信号：
/// 堆开得大的 JVM 在 Full GC 停顿期间连 accept 都会卡住，
/// 平时 0~1 毫秒的连接突然要几秒。那是它撑不住之前最早能看见的迹象，
/// 而那个时候端口还开着，只看「连不连得上」的探测仍然是绿的。
/// </summary>
public class EndpointSlowThresholdTests
{
    [Theory]
    [InlineData(3000, 2000, true)]   // 超了
    [InlineData(2000, 2000, true)]   // 正好踩线也算——阈值是「慢于等于」
    [InlineData(1, 2000, false)]
    [InlineData(5, null, false)]     // 留空＝不判
    [InlineData(5, 0, false)]        // 0 同样是不判：当成「0 毫秒以上都算慢」会让探测永远红着
    [InlineData(5, -1, false)]
    public void 判据只在阈值为正且耗时够大时才成立(int latency, int? threshold, bool expected)
    {
        Assert.Equal(expected, EndpointProber.IsTooSlow(latency, threshold));
    }

    /// <summary>TCP 探测连得上、又没超过阈值时，照常算通过，并把连接耗时记下来。</summary>
    [Fact]
    public async Task TCP连得上且不慢时算通过()
    {
        using var listener = Listen(out var port);

        var result = await new EndpointProber().ProbeAsync(
            TcpEndpoint(port, slowMilliseconds: 60_000), capturePreview: false, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        Assert.True(result.LatencyMs >= 0);
    }

    /// <summary>
    /// 阈值卡到 1 毫秒时，本机连接也会被判成慢——这一条钉的是「TCP 上这个阈值真的生效了」。
    /// 它原先是被服务层清空、探测器也不看的，改坏了不会报错，只会永远不响。
    /// </summary>
    [Fact]
    public async Task TCP超过阈值时算失败并写明耗时()
    {
        using var listener = Listen(out var port);

        var result = await new EndpointProber().ProbeAsync(
            TcpEndpoint(port, slowMilliseconds: 1), capturePreview: false, CancellationToken.None);

        // 本机 TCP 连接偶尔会快到 0 毫秒，那时按 1 毫秒的阈值确实不算慢——
        // 这里不去强求它一定慢，只钉住「真慢了的时候，失败信息要说清是慢，而不是笼统一个失败」。
        if (result.LatencyMs >= 1)
        {
            Assert.False(result.Success);
            Assert.Contains("连接耗时", result.Error);
            Assert.Contains("超过 1 毫秒", result.Error);
        }
        else
        {
            Assert.True(result.Success);
        }
    }

    /// <summary>不填阈值就是不判：本机连接再快也不该因为「快」或「慢」出岔子。</summary>
    [Fact]
    public async Task 不填阈值时不判慢()
    {
        using var listener = Listen(out var port);

        var result = await new EndpointProber().ProbeAsync(
            TcpEndpoint(port, slowMilliseconds: null), capturePreview: false, CancellationToken.None);

        Assert.True(result.Success, result.Error);
    }

    private static TcpListener Listen(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static MonitoredEndpoint TcpEndpoint(int port, int? slowMilliseconds) =>
        new()
        {
            Name = "本机测试端口",
            ProbeType = EndpointProbeType.Tcp,
            Target = "127.0.0.1",
            Port = port,
            TimeoutSeconds = 5,
            SlowMilliseconds = slowMilliseconds
        };
}
