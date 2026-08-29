using BackupMonitor.Agent;
using BackupMonitor.Infrastructure.Recognition;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Recognition;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// B1：固定槽位轮转覆盖的备份目录（周一…周日、Day1…Day7、Mon…Sun、01…07）。
///
/// 判成 N 个业务单元的后果里，最要命的是第三个：**周期性偏科失败完全看不见**。
/// MissedBackupWorker 是按任务判的，只要 N 个槽位里有任何一个是新的就不告警；
/// 而「周三」这个假单元自己的预检永远 passed（文件在、必需文件齐、早过稳定窗口）。
/// 于是「每周三的备份一直没成功」这件事，系统里没有任何一处会说出来。
///
/// 两道判据必须同时成立。只用名字判据是不够的：一个恰好按星期分的**业务**目录
/// 会被错认——因此这里的防回归用例（账套目录、时间聚集的星期目录）与正例同等重要。
/// </summary>
public sealed class RotatingSlotsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bm-rot-" + Guid.NewGuid().ToString("N"));

    public RotatingSlotsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>建一个槽位，并把里面文件的 mtime 设成「daysAgo 天前」。</summary>
    private void Slot(string name, double daysAgo)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "db.bak");
        File.WriteAllText(path, new string('x', 4096));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysAgo));
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-daysAgo));
    }

    private RecognizerProposalDto Infer()
    {
        var browser = new DirectoryBrowser(
            Options.Create(new AgentOptions()), NullLogger<DirectoryBrowser>.Instance);
        var snapshot = browser.Browse(
            new BrowsePathCommandPayload { Path = _root, MaxDepth = 3, MaxEntries = 3000 },
            CancellationToken.None);
        return StructureInference.Infer(SnapshotNode.FromSnapshot(snapshot), DateTime.UtcNow, snapshot.MaxDepth);
    }

    [Fact]
    public void 星期目录且时间依次错开时判为轮换副本()
    {
        string[] week = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
        for (var i = 0; i < week.Length; i++)
            Slot(week[i], daysAgo: week.Length - 1 - i);

        var proposal = Infer();

        Assert.Equal("latest_directory", proposal.RecognizerType);
        Assert.Contains("轮换副本", proposal.Summary, StringComparison.Ordinal);

        // 逃生门必须在场：自适应判定没有逃生门就是死局。
        Assert.Contains(proposal.Evidence, e => e.Contains("子目录单元", StringComparison.Ordinal));

        // 预演只产出 1 个单元，而不是 7 个。
        var preview = Preview(proposal);
        Assert.Single(preview.Units);
    }

    [Theory]
    [InlineData("Day1", "Day2", "Day3", "Day4", "Day5", "Day6", "Day7")]
    [InlineData("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")]
    [InlineData("01", "02", "03", "04", "05", "06", "07")]
    public void 三种词表都能认出来(params string[] slots)
    {
        for (var i = 0; i < slots.Length; i++)
            Slot(slots[i], daysAgo: slots.Length - 1 - i);

        var proposal = Infer();

        Assert.Equal("latest_directory", proposal.RecognizerType);
        Assert.Contains("轮换副本", proposal.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 防回归：18 个账套是同一批跑的，最新时间聚集在同一小时内 —— 必须仍走情形三。
    /// 这一条比正例更重要：把真实业务单元判成轮换副本，等于把 17 个账套踢出监控范围。
    /// </summary>
    [Fact]
    public void 账套目录时间聚集时行为不变()
    {
        for (var i = 1; i <= 7; i++)
            Slot($"账套{i:000}", daysAgo: i * 0.01);

        var proposal = Infer();

        Assert.Equal("subdirectory_units", proposal.RecognizerType);
        Assert.DoesNotContain("轮换副本", proposal.Summary, StringComparison.Ordinal);
    }

    /// <summary>名字命中词表、但时间全部聚集：判定不成立，落回情形三。</summary>
    [Fact]
    public void 星期目录但时间聚集时判定不成立()
    {
        string[] week = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
        foreach (var name in week)
            Slot(name, daysAgo: 0.01);

        var proposal = Infer();

        Assert.Equal("subdirectory_units", proposal.RecognizerType);
    }

    /// <summary>纯序号那一类要收窄：最大值超过 31 的编号（账套 1…40）不认。</summary>
    [Fact]
    public void 纯序号超过三十一时不认作轮转()
    {
        for (var i = 1; i <= 40; i++)
            Slot(i.ToString("00"), daysAgo: 40 - i);

        var proposal = Infer();

        Assert.Equal("subdirectory_units", proposal.RecognizerType);
    }

    /// <summary>ABC 明确不收：与真实业务分组（车间 A/B/C）无法区分，误判代价高于收益。</summary>
    [Fact]
    public void 字母槽位明确不收()
    {
        Slot("A", 2);
        Slot("B", 1);
        Slot("C", 0);

        var proposal = Infer();

        Assert.Equal("subdirectory_units", proposal.RecognizerType);
    }

    private RecognizerPreviewDto Preview(RecognizerProposalDto proposal)
    {
        var browser = new DirectoryBrowser(
            Options.Create(new AgentOptions()), NullLogger<DirectoryBrowser>.Instance);
        var snapshot = browser.Browse(
            new BrowsePathCommandPayload { Path = _root, MaxDepth = 3, MaxEntries = 3000 },
            CancellationToken.None);
        return SnapshotRecognizer.Preview(
            SnapshotNode.FromSnapshot(snapshot),
            proposal.RecognizerType,
            RecognizerRules.Parse(proposal.RecognizerConfig),
            stabilityIntervalSeconds: 0,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);
    }
}
