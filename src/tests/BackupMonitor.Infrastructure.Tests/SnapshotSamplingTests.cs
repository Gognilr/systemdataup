using BackupMonitor.Agent;
using BackupMonitor.Infrastructure.Recognition;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// B2：快照按字典序深度优先截断，而所有结构判据都是比例。
///
/// 这两件事单独看都合理，凑在一起就不成立了：**用字典序截断的样本去算比例**。
/// 18 个账套 × 7 天 × 20 个文件 = 2520 条已经贴着 3000 的预算，一旦触顶，
/// 被丢掉的永远是字典序靠后的那一批，而不是随机的一批。
///
/// 结论不是「说自己没看全」（那本来就有），而是在自己标了「没看全」之后，
/// 仍然用一个**有偏**样本算出比例并据此下结论。HasGaps 只能表达「不完整」，
/// 表达不了「有偏」。
/// </summary>
public sealed class SnapshotSamplingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bm-sample-" + Guid.NewGuid().ToString("N"));

    public SnapshotSamplingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private BrowseSnapshotDto Browse(int maxDepth = 3, int maxEntries = 3000)
    {
        var browser = new DirectoryBrowser(
            Options.Create(new AgentOptions()), NullLogger<DirectoryBrowser>.Instance);
        return browser.Browse(
            new BrowsePathCommandPayload { Path = _root, MaxDepth = maxDepth, MaxEntries = maxEntries },
            CancellationToken.None);
    }

    /// <summary>
    /// 一个胖目录不能把整层的预算吃掉。
    /// 改动前：ZT001 的 2500 个文件吃光预算，后面 17 个账套只抓到零星几个，
    /// unitDateRatio 用 3–4 个样本算出来。
    /// 改动后：18 个账套全部出现，ZT001 标 Sampled 且 FileCount 是真实的 2500。
    /// </summary>
    [Fact]
    public void 胖目录不再吃掉整层预算()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ZT001"));
        for (var i = 0; i < 2500; i++)
            File.WriteAllText(Path.Combine(_root, "ZT001", $"archive_{i:0000}.bak"), "x");

        for (var unit = 2; unit <= 18; unit++)
        {
            var dir = Path.Combine(_root, $"ZT{unit:000}");
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 20; i++)
                File.WriteAllText(Path.Combine(dir, $"db_{i:00}.bak"), "xxxx");
        }

        var snapshot = Browse();
        var directories = snapshot.Entries.Where(e => e.IsDirectory).ToList();

        Assert.Equal(18, directories.Count);

        var fat = Assert.Single(directories, d => d.Name == "ZT001");
        Assert.True(fat.Sampled, "抓不完的目录要标 Sampled");
        Assert.Equal(2500, fat.FileCount);
        // 明细只带回来一部分，但个数与总字节是真实的。
        Assert.True(fat.Children.Count(c => !c.IsDirectory) < 2500);

        // 其余账套没触顶，行为与从前逐字节相同：20 个文件全带回来，不标 Sampled。
        var normal = Assert.Single(directories, d => d.Name == "ZT018");
        Assert.False(normal.Sampled);
        Assert.Equal(20, normal.Children.Count(c => !c.IsDirectory));
    }

    /// <summary>
    /// Sampled 与 HasGaps 分开：抽样过的快照每一层都有代表性样本，结论可信，
    /// **不扣置信度**，但要在 Evidence 里说明。再按「不完整」扣分等于惩罚一个已经修好的问题。
    /// </summary>
    [Fact]
    public void 抽样不扣置信度但要说明()
    {
        for (var unit = 1; unit <= 6; unit++)
        {
            var dir = Path.Combine(_root, $"ZT{unit:000}");
            Directory.CreateDirectory(dir);
            var count = unit == 1 ? 800 : 20;
            for (var i = 0; i < count; i++)
                File.WriteAllText(Path.Combine(dir, $"db_{i:0000}.bak"), "xxxx");
        }

        var snapshot = Browse();
        var root = SnapshotNode.FromSnapshot(snapshot);
        var proposal = StructureInference.Infer(root, snapshot.CapturedAt, snapshot.MaxDepth);

        Assert.True(root.HasSampling);
        Assert.False(root.HasGaps, "抽样不等于没抓全——两者混在一起就会对一个已经修好的问题继续扣分");
        Assert.Contains(proposal.Evidence, e => e.Contains("样本", StringComparison.Ordinal));
        Assert.False(proposal.NeedsDeeperScan);
    }

    /// <summary>未触顶的小快照行为完全不变——这条是 B2 的边界，不是它要改的东西。</summary>
    [Fact]
    public void 小快照行为完全不变()
    {
        for (var unit = 1; unit <= 3; unit++)
        {
            var dir = Path.Combine(_root, $"ZT{unit:000}");
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(dir, $"db_{i:00}.bak"), "xxxx");
        }

        var snapshot = Browse();

        Assert.False(snapshot.Truncated);
        Assert.All(snapshot.Entries.Where(e => e.IsDirectory), d =>
        {
            Assert.False(d.Sampled);
            Assert.Equal(5, d.Children.Count(c => !c.IsDirectory));
        });
    }

    /// <summary>逐层推进：第 2 层的目录不会因为第 1 层的某一支特别深就看不见。</summary>
    [Fact]
    public void 每一层都看得见()
    {
        // 第一支特别深、条目特别多
        for (var i = 0; i < 600; i++)
            WriteDeep($@"A\深层\更深\{i:000}.bak");

        for (var unit = 2; unit <= 10; unit++)
            WriteDeep($@"ZT{unit:000}\20260825\db.bak");

        var snapshot = Browse(maxDepth: 3);
        var level1 = snapshot.Entries.Where(e => e.IsDirectory).Select(e => e.Name).ToList();

        Assert.Equal(10, level1.Count);
        // 第 2 层：每个账套的日期目录都要在
        foreach (var unit in Enumerable.Range(2, 9))
        {
            var node = Assert.Single(snapshot.Entries, e => e.Name == $"ZT{unit:000}");
            Assert.Contains(node.Children, c => c.Name == "20260825");
        }
    }

    private void WriteDeep(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "xxxx");
    }
}
