namespace BackupMonitor.Agent.Tests;

/// <summary>
/// 上传限速。
///
/// 限速算错的表现是「传得慢」——所有故障里最不容易被发现的那一种：
/// 没有报错、没有告警，只是备份窗口悄悄从两小时变成两天。
/// 因此这里验的是换算和等待时长本身，而不是「大概能限住」。
/// </summary>
public class ChunkThrottleTests
{
    private const int OneMbPerSecond = 1024;   // kbps

    [Fact]
    public void 没配限速就不等()
    {
        // 限速是可选项。没配的时候多等哪怕一秒，都是白白拖长备份窗口。
        Assert.Equal(0, AgentWorker.ChunkThrottle.ToBytesPerSecond(null));
        Assert.Equal(0, AgentWorker.ChunkThrottle.ToBytesPerSecond(0));
        Assert.Equal(TimeSpan.Zero,
            AgentWorker.ChunkThrottle.ComputeDelay(0, 100 * 1024 * 1024, TimeSpan.Zero));
    }

    [Fact]
    public void 负数限速当作没配()
    {
        // 配置里手抖填了负数，不该变成「反向加速」或者直接崩，按没限速处理。
        Assert.Equal(0, AgentWorker.ChunkThrottle.ToBytesPerSecond(-1));
    }

    [Fact]
    public void 千字节每秒按一零二四换算()
    {
        Assert.Equal(1024L * 1024, AgentWorker.ChunkThrottle.ToBytesPerSecond(OneMbPerSecond));
    }

    [Fact]
    public void 极大的限速值不会溢出成极小的限速值()
    {
        // kbps × 1024 如果按 int 算，超过 2^21 就溢出。绕回来可能是个很小的正数——
        // 「限速 4 GB/s」实际会变成限速 1 KB/s，而界面上完全看不出来。
        const int hugeKbps = 4 * 1024 * 1024 + 1;   // int 乘法在这里会绕回 1024

        var bytesPerSecond = AgentWorker.ChunkThrottle.ToBytesPerSecond(hugeKbps);

        Assert.Equal((long)hugeKbps * 1024, bytesPerSecond);
        Assert.True(bytesPerSecond > int.MaxValue, "换算必须在 long 上做，否则会绕回一个很小的正数");
    }

    [Fact]
    public void 传得比限速快就等到该等的时刻()
    {
        // 1 MB/s 的限速下已经传了 5 MB，本该花 5 秒，实际只过了 2 秒，
        // 因此还要再等 3 秒把平均速度压回去。
        var delay = AgentWorker.ChunkThrottle.ComputeDelay(
            1024 * 1024, 5 * 1024 * 1024, TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void 本来就比限速慢就不等()
    {
        // 链路自己就跑不到限速值时再叠一层等待，等于二次限速。
        var delay = AgentWorker.ChunkThrottle.ComputeDelay(
            1024 * 1024, 5 * 1024 * 1024, TimeSpan.FromSeconds(9));

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void 限速是会话级平均而不是每块独立算()
    {
        // 前面被网络抖动拖慢的时间要能抵消后面传得快的块，
        // 否则一次抖动之后的每一块都要陪着多等一次。
        var ahead = AgentWorker.ChunkThrottle.ComputeDelay(
            1024 * 1024, 10 * 1024 * 1024, TimeSpan.FromSeconds(4));
        var behind = AgentWorker.ChunkThrottle.ComputeDelay(
            1024 * 1024, 10 * 1024 * 1024, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(6), ahead);
        Assert.Equal(TimeSpan.Zero, behind);
    }

    [Fact]
    public void 荒唐的限速值不会把等待算到溢出()
    {
        // TimeSpan.FromSeconds 超出表示范围会抛 OverflowException。
        // 同一个文件里的 ProgressReporter 刚因为一次 TimeSpan 溢出把所有上传打挂过，
        // 这里必须夹住而不是抛——限速配错了应该表现为「等得久」，不是「备份失败」。
        var delay = AgentWorker.ChunkThrottle.ComputeDelay(
            bytesPerSecond: 1, sentBytes: long.MaxValue, elapsed: TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromDays(1), delay);
    }
}
