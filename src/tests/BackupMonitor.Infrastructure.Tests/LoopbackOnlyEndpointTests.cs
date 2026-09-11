using System.Net;
using BackupMonitor.Shared.Security;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 本机自检端点（/health/db/local）的准入判断。
///
/// 这条判断是那个端点唯一的门：它匿名可访问，凭的就是「对端是本机」。
/// 判错一次的两个方向都不轻——放宽了会把数据库自检结果开给内网，
/// 收紧了则会让服务端托盘与服务管理台常年显示「数据库未自检」。
///
/// 特别要钉住 IPv4-mapped IPv6（::ffff:127.0.0.1）：Windows 套接字在双栈监听下
/// 就是这样表示 IPv4 对端的，而 <see cref="IPAddress.IsLoopback"/> 对这种写法直接返回 false。
/// 少了这一次映射，本机访问会被当成外人挡掉——而且只在双栈的机器上挡。
/// </summary>
public class LoopbackOnlyEndpointTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_peers_are_allowed(string address)
    {
        Assert.True(LanNetworkPolicy.IsLoopback(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("192.168.1.20")]   // 同一内网的另一台机器：IsPrivateOrLoopback 认它，这里不认
    [InlineData("10.0.0.7")]
    [InlineData("169.254.3.4")]
    [InlineData("203.0.113.9")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.20")]
    public void Non_loopback_peers_are_refused(string address)
    {
        Assert.False(LanNetworkPolicy.IsLoopback(IPAddress.Parse(address)));
    }

    /// <summary>
    /// 取不到对端地址时按「不是本机」处理。Kestrel 在连接已经断开的情况下
    /// RemoteIpAddress 可以是 null，那时宁可少答一次，不能误开。
    /// </summary>
    [Fact]
    public void Missing_peer_address_is_refused()
    {
        Assert.False(LanNetworkPolicy.IsLoopback(null));
    }
}
