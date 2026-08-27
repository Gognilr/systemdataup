
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Agent.Tests;

/// <summary>
/// 上传进度上报器。
///
/// 这个类只做一件事：决定「这一块传完之后，要不要往服务端发一条进度」。
/// 它是纯装饰功能——发不发、发得准不准，都不该影响备份本身。
///
/// 但它曾经把所有备份都搞挂过：<c>_lastReportedAt</c> 的初值写成了
/// <c>TimeSpan.MinValue</c>，而 <c>Elapsed - TimeSpan.MinValue</c> 必然溢出 Int64，
/// 抛 <c>OverflowException("TimeSpan overflowed because the duration is too long.")</c>。
/// ReportAsync 在每块传完后都会调，于是第一块一传完就炸；
/// 更糟的是当时 try 只包住了 HTTP 调用，没包住这段节流判断，
/// 异常一路冒到 ExecuteUploadAsync 的兜底 catch，指令判 upload_failed。
/// 结果是：一个纯展示功能让所有机器的所有上传 100% 失败。
///
/// 这组用例就是为了这件事不再发生第二次。
/// </summary>
public class ProgressReporterTests
{
    private const long TotalBytes = 1000;

    [Fact]
    public async Task 第一块传完就上报而不是先干等三十秒()
    {
        // 这一条同时是那个溢出缺陷的回归用例：改坏之后这里会直接抛
        // OverflowException，而不是断言失败。
        var sink = new RecordingSink();
        var reporter = NewReporter(sink);

        await reporter.ReportAsync(100, @"D:\data\a.bak", CancellationToken.None);

        var sent = Assert.Single(sink.Sent);
        Assert.Equal(10m, sent.Percent);
        // 传大文件的人最需要的就是开头那一下反馈：进度条动了，说明链路是通的。
        // 让第一次也走 30 秒节流，等于把最有价值的那条信息压掉。
    }

    [Fact]
    public async Task 涨幅不够就不重复上报()
    {
        var sink = new RecordingSink();
        var reporter = NewReporter(sink);
        await reporter.ReportAsync(100, "a", CancellationToken.None);

        // 只涨了 1 个点，没到 5 个点的门槛。
        await reporter.ReportAsync(110, "a", CancellationToken.None);

        Assert.Single(sink.Sent);
    }

    [Fact]
    public async Task 间隔不够就不重复上报()
    {
        var sink = new RecordingSink();
        var reporter = NewReporter(sink);
        await reporter.ReportAsync(100, "a", CancellationToken.None);

        // 涨幅足够（10 → 90），但距上次连一秒都不到。
        // 50GB / 8MB = 6400 块，每块都发会把 commands 表写爆，
        // 而人并不需要每 8MB 看一次。
        await reporter.ReportAsync(900, "a", CancellationToken.None);

        Assert.Single(sink.Sent);
    }

    [Fact]
    public async Task 上报失败不会把正在进行的上传搞挂()
    {
        var reporter = NewReporter(new ThrowingSink());

        // 服务端 500、网络抖一下、令牌过期——上报这条路上的任何失败，
        // 都不该让一次已经传了两小时的上传前功尽弃。
        var ex = await Record.ExceptionAsync(
            () => reporter.ReportAsync(100, "a", CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task 节流判断自己抛异常也不会漏出去()
    {
        // 当年就栽在这里：try 只包住了 HTTP 调用，节流判断在 try 之外。
        // 用 totalBytes = 0 逼百分比那一步算出非法值，验证 try 确实包住了整个方法体。
        var sink = new RecordingSink();
        var reporter = new AgentWorker.ProgressReporter(
            sink.SendAsync, NullLogger.Instance, Guid.NewGuid(), totalBytes: 0);

        var ex = await Record.ExceptionAsync(
            () => reporter.ReportAsync(100, "a", CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task 取消要照常抛出而不是被当成上报失败吞掉()
    {
        // 停机和「上报失败」是两回事：前者必须让整条上传链路一起停下来收尾，
        // 吞掉它会让 Agent 在服务停止之后还在傻传。
        var reporter = NewReporter(new CancelingSink());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reporter.ReportAsync(100, "a", CancellationToken.None));
    }

    [Fact]
    public async Task 百分比被夹在零到一百之间()
    {
        // 续传时 sentBytes 只累计「这一次真正传的量」，而 totalBytes 取自候选集，
        // 两者口径不同，理论上可能算出超过 100 的数。界面上出现 137% 会让人
        // 直接怀疑整套数字都是错的。
        var sink = new RecordingSink();
        var reporter = NewReporter(sink);

        await reporter.ReportAsync(5000, "a", CancellationToken.None);

        Assert.Equal(100m, Assert.Single(sink.Sent).Percent);
    }

    // ---------- 基础设施 ----------

    private static AgentWorker.ProgressReporter NewReporter(ISink sink) =>
        new(sink.SendAsync, NullLogger.Instance, Guid.NewGuid(), TotalBytes);

    private interface ISink
    {
        Task SendAsync(CommandProgressRequest request, CancellationToken ct);
    }

    private sealed class RecordingSink : ISink
    {
        public List<CommandProgressRequest> Sent { get; } = [];

        public Task SendAsync(CommandProgressRequest request, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : ISink
    {
        public Task SendAsync(CommandProgressRequest request, CancellationToken ct) =>
            throw new HttpRequestException("服务端暂时不可达");
    }

    private sealed class CancelingSink : ISink
    {
        public Task SendAsync(CommandProgressRequest request, CancellationToken ct) =>
            throw new OperationCanceledException();
    }
}
