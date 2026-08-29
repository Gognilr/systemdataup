using BackupMonitor.Agent;
using BackupMonitor.Infrastructure.Recognition;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Recognition;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// B6：分卷归档用「个数一致」这把错尺子。
///
/// 「个数一致」那条判据本身是对的，来源明确：U8 的 UFFile 是按年度分的附件库，
/// 各账套启用年度不同，个数波动是正常形态，拿它当判据只会制造假警报。
/// 但对分卷用反了——分卷数天然随数据量浮动，于是 `db_*.7z` 永远个数不一致、
/// 永远不被推荐、**缺一卷整份无法恢复却不告警**。
///
/// 换的是尺子（连续性 vs 个数稳定性），不是推翻原判据：U8 的附件库不该被当成分卷。
/// （附件库自己的判据后来也改了——个数不参与判定，只看「至少有一个」，见下面那条用例。）
/// </summary>
public sealed class VolumeArchiveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bm-vol-" + Guid.NewGuid().ToString("N"));

    public VolumeArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ---------- ParseVolume ----------

    [Theory]
    [InlineData("db.7z.001", "db.7z", 1)]
    [InlineData("db.7z.003", "db.7z", 3)]
    [InlineData("data.zip.012", "data.zip", 12)]
    [InlineData("db.z01", "db.zip", 1)]
    [InlineData("db.r02", "db.rar", 2)]
    [InlineData("db.part1.rar", "db.rar", 1)]
    [InlineData("db.part03.rar", "db.rar", 3)]
    public void 分卷形态能解析(string name, string stem, int index)
    {
        var volume = FileNamePattern.ParseVolume(name);
        Assert.NotNull(volume);
        Assert.Equal(stem, volume!.Value.Stem);
        Assert.Equal(index, volume.Value.Index);
    }

    [Theory]
    [InlineData("UFDATA.BAK")]
    [InlineData("UFFile_001_2022.dat")]
    [InlineData("backup.tar.gz")]
    [InlineData("2026-08-30@02_00.zip")]
    public void 非分卷不误判(string name) =>
        Assert.Null(FileNamePattern.ParseVolume(name));

    // ---------- 复合扩展名 ----------

    [Theory]
    [InlineData("backup.tar.gz", ".tar.gz", "backup")]
    [InlineData("db.sql.gz", ".sql.gz", "db")]
    [InlineData("db.bak", ".bak", "db")]
    // 主干里带点的普通文件不能被误合并成扩展名。
    [InlineData("2026-08-30@02_00.zip", ".zip", "2026-08-30@02_00")]
    [InlineData(".gitignore", "", ".gitignore")]
    public void 复合扩展名整体识别(string name, string extension, string baseName)
    {
        Assert.Equal(extension, FileNamePattern.Extension(name));
        Assert.Equal(baseName, FileNamePattern.BaseName(name));
    }

    // ---------- 推断端：连续性判据 ----------

    [Fact]
    public void 三卷齐全时推荐为必需()
    {
        for (var unit = 1; unit <= 3; unit++)
            for (var volume = 1; volume <= 3; volume++)
                Write($@"ZT{unit:000}\db.7z.{volume:000}");

        var proposal = Infer();

        var candidate = FindVolumeCandidate(proposal);
        Assert.True(candidate.Recommended, candidate.NotRecommendedReason);
        Assert.True(RecognizerRules.Parse(proposal.RecognizerConfig).VolumeContinuity,
            "推荐了分卷模式就必须写 volumeContinuity，否则扫描端只会检查「这个模式有文件在场」");
    }

    /// <summary>
    /// 各单元卷数不同（3 卷 / 5 卷）但各自连续 → **仍然推荐**。
    /// 这正是与「个数一致」判据的区别所在。
    /// </summary>
    [Fact]
    public void 各单元卷数不同但各自连续时仍然推荐()
    {
        int[] counts = [3, 5, 4];
        for (var unit = 0; unit < counts.Length; unit++)
            for (var volume = 1; volume <= counts[unit]; volume++)
                Write($@"ZT{unit + 1:000}\db.7z.{volume:000}");

        var proposal = Infer();

        var candidate = FindVolumeCandidate(proposal);
        Assert.True(candidate.Recommended, candidate.NotRecommendedReason);
        Assert.NotEqual(candidate.CountPerUnitMin, candidate.CountPerUnitMax);
    }

    [Fact]
    public void 抠掉第二卷后不推荐并点名缺第几卷()
    {
        for (var unit = 1; unit <= 3; unit++)
            for (var volume = 1; volume <= 3; volume++)
            {
                if (unit == 2 && volume == 2)
                    continue;
                Write($@"ZT{unit:000}\db.7z.{volume:000}");
            }

        var proposal = Infer();

        var candidate = FindVolumeCandidate(proposal);
        Assert.False(candidate.Recommended);
        Assert.Contains("缺第 2 卷", candidate.NotRecommendedReason ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// 防回归：U8 的 UFFile 附件库不是分卷，走的是第二档的归一化模式，
    /// 判据是「至少有一个」而不是卷号连续——所以不写 volumeContinuity，
    /// 也不会因为各账套个数不同（5 / 3 / 4）就被判成缺卷。这是 B6 的边界。
    /// </summary>
    [Fact]
    public void U8附件库不是分卷不做连续性检查()
    {
        int[] fileCounts = [5, 3, 4];
        for (var unit = 0; unit < fileCounts.Length; unit++)
        {
            Write($@"ZT{unit + 1:000}\UFDATA.BAK");
            // 附件库文件名里带账套号：各账套的文件名互不相同，因此走的是第二档的
            // 归一化模式（UFFile_*.dat），而不是第一档的精确文件名。
            // 个数随启用年度天然不同（5 / 3 / 4）——这正是那条「个数一致」判据要挡住的形态。
            for (var year = 0; year < fileCounts[unit]; year++)
                Write($@"ZT{unit + 1:000}\UFFile_{unit + 1:000}_{2022 + year}.dat");
        }

        var proposal = Infer();

        var uffile = proposal.RequiredFileCandidates
            .FirstOrDefault(c => c.Pattern.Contains("UFFile", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(uffile);
        // 个数不同不影响推荐：判的是在场，不是个数。
        Assert.True(uffile!.Recommended);
        Assert.Equal(3, uffile.CountPerUnitMin);
        Assert.Equal(5, uffile.CountPerUnitMax);

        // 不是分卷 → 不写 volumeContinuity，扫描端不做多余的连续性检查。
        Assert.False(RecognizerRules.Parse(proposal.RecognizerConfig).VolumeContinuity);
    }

    // ---------- 扫描端：两侧共用同一段判定 ----------

    [Fact]
    public void 扫描端缺卷时判必需文件缺失()
    {
        var rules = RecognizerRules.Parse("""{"volumeContinuity":true}""");

        Assert.Null(rules.FindVolumeGap(["db.7z.001", "db.7z.002", "db.7z.003"]));
        Assert.Contains("缺第 2 卷", rules.FindVolumeGap(["db.7z.001", "db.7z.003"]) ?? "", StringComparison.Ordinal);
    }

    /// <summary>没开 volumeContinuity 时永远不检查——这是一条选择开启的判据。</summary>
    [Fact]
    public void 没开连续性检查时不判缺卷()
    {
        var rules = RecognizerRules.Parse("{}");
        Assert.Null(rules.FindVolumeGap(["db.7z.001", "db.7z.003"]));
    }

    /// <summary>.z01/.z02 形态的末卷是 .zip 本身，它缺席同样解不开，要单独说。</summary>
    [Fact]
    public void 缺末卷时单独点名()
    {
        var rules = RecognizerRules.Parse("""{"volumeContinuity":true}""");

        Assert.Null(rules.FindVolumeGap(["db.z01", "db.z02", "db.zip"]));
        Assert.Contains("末卷", rules.FindVolumeGap(["db.z01", "db.z02"]) ?? "", StringComparison.Ordinal);
    }

    // ---------- 辅助 ----------

    private void Write(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', 4096));
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

    private static RequiredFileCandidateDto FindVolumeCandidate(RecognizerProposalDto proposal)
    {
        var candidate = proposal.RequiredFileCandidates
            .FirstOrDefault(c => c.Pattern.Contains("7z", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(candidate);
        return candidate!;
    }
}
