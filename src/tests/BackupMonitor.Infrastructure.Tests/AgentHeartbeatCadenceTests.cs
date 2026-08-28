using BackupMonitor.Agent;
using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// Agent 心跳节奏与快照免报（审计 B-09）。
///
/// 这两条都是纯本地行为，不需要数据库：
/// 一、同一台机器连续两次采样，磁盘/服务/会话三块没变就不该重复上报；
/// 二、第一次心跳必须带全量快照——服务端此时对这台机器一无所知。
///
/// 归进 postgres 集合不是因为要用数据库，而是要**避开并行**：
/// 快照摘要把磁盘可用空间按 64MB 分桶，而库测试会起本地 PostgreSQL 实例、
/// 写暂存文件，同一块盘上轻易就跨掉一个桶——于是「两次采样之间机器没有变化」
/// 这个前提在并行跑全量时不成立，本类会随机变红。
/// xUnit 同一集合内的测试类串行执行，把它放进来即可让前提重新成立。
/// 不在构造函数里接夹具：本类确实一行数据库都不碰。
/// </summary>
[Collection("postgres")]
public sealed class AgentHeartbeatCadenceTests
{
    [Fact]
    public void 首次心跳带全量快照而第二次不重复上报()
    {
        var probe = new SystemProbe();

        var first = probe.BuildHeartbeat(0, null, []);
        Assert.False(first.SnapshotUnchanged);
        Assert.NotNull(first.Disks);          // 服务端此时对这台机器一无所知，必须给全量
        Assert.NotNull(first.UserSessions);

        // 两次采样之间机器不会有实质变化（磁盘可用空间按 64MB 粒度取整，
        // 正是为了让几 KB 的抖动不至于让摘要永不相同）。
        var second = probe.BuildHeartbeat(0, null, []);
        Assert.True(second.SnapshotUnchanged);
        Assert.Null(second.Disks);
        Assert.Null(second.ServiceStates);
        Assert.Null(second.UserSessions);

        // 免报的只有那几块。指标每次都不同，本来就要带。
        Assert.NotNull(second.Metrics);
    }

    [Fact]
    public void 网卡地址随快照一起上报且不重复发送()
    {
        // 原先这份数据只在注册请求里出现过一次，之后永不刷新：
        // 换网段、DHCP 续租之后库里那份就是错的，而界面看不出它是旧的。
        // 现在它并进快照摘要，跟着「变了才发」的节奏走。
        var probe = new SystemProbe();

        var first = probe.BuildHeartbeat(0, null, []);
        Assert.NotNull(first.IpAddresses);

        var second = probe.BuildHeartbeat(0, null, []);
        Assert.True(second.SnapshotUnchanged);
        Assert.Null(second.IpAddresses);
    }

    [Fact]
    public void 活动指令列表随心跳一起上报()
    {
        var probe = new SystemProbe();
        var commandId = Guid.NewGuid();

        var heartbeat = probe.BuildHeartbeat(7, null, [commandId]);

        Assert.Equal(7, heartbeat.ConfigVersion);
        Assert.Equal([commandId], heartbeat.ActiveCommands);
    }

    [Fact]
    public void 升级包地址只认自己的服务端()
    {
        // 与 CommandSignatureTests 里那条是同一道锁的两侧：
        // 那边验的是签名覆盖 payload，这边验的是纵深防御本身。
        Assert.True(AgentWorker.IsSameServerAuthority(
            new Uri("https://backup.internal:8443/downloads/a.zip"), "https://backup.internal:8443"));
        Assert.False(AgentWorker.IsSameServerAuthority(
            new Uri("https://evil.example.com/a.zip"), "https://backup.internal:8443"));
    }
}
