using BackupMonitor.Infrastructure.Services;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 传输速度采样与卡死判定。
///
/// 这两件事回答的是同一个问题：一份几十 GB 的备份传了半小时，
/// 它是在慢慢传，还是已经不动了？答错任何一边的代价都很实际——
/// 判松了真卡死永远不报，判紧了每条慢传输都标成故障，
/// 而人只要被误报两次就再也不看这个标记了。
///
/// 全是纯计算，不碰数据库，因此这组用例不需要 Docker。
/// </summary>
public class UploadProgressSamplerTests
{
    private static readonly DateTime T0 = new(2026, 8, 27, 3, 0, 0, DateTimeKind.Utc);
    private const int ChunkSize = 8 * 1024 * 1024;

    [Fact]
    public void 第一次观测给不出速度()
    {
        var sampler = new UploadRateSampler();

        // 库里只有累计字节数，没有上一次的基准就无从算增量。
        // 这里必须是 null 而不是 0：0 会被上层当成「速度是零」，进而画成卡死。
        Assert.Null(sampler.Observe(Guid.NewGuid(), 1024, T0));
    }

    [Fact]
    public void 两次观测之间的增量就是速度()
    {
        var sampler = new UploadRateSampler();
        var id = Guid.NewGuid();
        sampler.Observe(id, 0, T0);

        var rate = sampler.Observe(id, 10 * 1024 * 1024, T0.AddSeconds(10));

        Assert.Equal(1024 * 1024, rate);
    }

    [Fact]
    public void 相邻太近的两次观测复用上一次的速度()
    {
        var sampler = new UploadRateSampler();
        var id = Guid.NewGuid();
        sampler.Observe(id, 0, T0);
        var settled = sampler.Observe(id, 10 * 1024 * 1024, T0.AddSeconds(10));

        // 多开两个浏览器就会出现毫秒级的相邻两次请求。拿那个时间差去除，
        // 算出来的速度会在几十倍之间乱跳，看着像链路故障，其实是采样噪声。
        var tooSoon = sampler.Observe(id, 10 * 1024 * 1024 + 4096, T0.AddSeconds(10.2));

        Assert.Equal(settled, tooSoon);
    }

    [Fact]
    public void 字节数回退时速度按零处理而不是负数()
    {
        var sampler = new UploadRateSampler();
        var id = Guid.NewGuid();
        sampler.Observe(id, 50 * 1024 * 1024, T0);

        // 续传会话重建后 uploaded_bytes 可能比上次看到的小。
        // 不兜住会算出负速度，界面上就是一个「-3.2 MB/s」。
        var rate = sampler.Observe(id, 10 * 1024 * 1024, T0.AddSeconds(10));

        Assert.Equal(0, rate);
    }

    [Fact]
    public void 会话结束后采样记录被清掉()
    {
        var sampler = new UploadRateSampler();
        var id = Guid.NewGuid();
        sampler.Observe(id, 1024, T0);

        sampler.Forget([]);

        // 清干净的判据：同一个 id 再观测又变回「第一次」。
        // 不清的话这张表会随历史会话数一直涨，而它是个进程级单例。
        Assert.Null(sampler.Observe(id, 2048, T0.AddSeconds(10)));
    }

    [Fact]
    public void 还在传的会话不会被误清()
    {
        var sampler = new UploadRateSampler();
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();
        sampler.Observe(live, 0, T0);
        sampler.Observe(gone, 0, T0);

        sampler.Forget([live]);

        Assert.NotNull(sampler.Observe(live, 1024 * 1024, T0.AddSeconds(10)));
        Assert.Null(sampler.Observe(gone, 1024 * 1024, T0.AddSeconds(10)));
    }

    [Fact]
    public void 速度未知时用下限阈值()
    {
        // 刚建的会话一个字节都还没传，算不出速度，此时只能用固定下限。
        Assert.Equal(TimeSpan.FromSeconds(180), UploadProgressService.StallThreshold(ChunkSize, null));
    }

    [Fact]
    public void 快链路上阈值不低于三分钟()
    {
        // 100 MB/s 上传一个 8MB 分块不到 0.1 秒，三倍余量也才 0.3 秒。
        // 真按这个判，一次正常的网络抖动就会被报成卡死。
        var threshold = UploadProgressService.StallThreshold(ChunkSize, 100L * 1024 * 1024);

        Assert.Equal(TimeSpan.FromSeconds(180), threshold);
    }

    [Fact]
    public void 链路越慢阈值越宽()
    {
        // 50 KB/s 传一个 8MB 分块要 164 秒，这期间 last_activity_at 本来就不动。
        // 固定 180 秒会把这条正常的窄带传输报成卡死——而窄带正是最需要看进度的场景。
        var threshold = UploadProgressService.StallThreshold(ChunkSize, 50 * 1024);

        Assert.True(threshold > TimeSpan.FromSeconds(180),
            $"慢链路的阈值必须比下限宽，实际 {threshold}");
        Assert.Equal(TimeSpan.FromSeconds(8.0 * 1024 * 1024 / (50 * 1024) * 3), threshold);
    }

    [Fact]
    public void 阈值有上限()
    {
        // 链路再慢也不能无限放宽，否则一条真死掉的会话永远不会被标出来。
        var threshold = UploadProgressService.StallThreshold(ChunkSize, 1);

        Assert.Equal(TimeSpan.FromMinutes(30), threshold);
    }
}
