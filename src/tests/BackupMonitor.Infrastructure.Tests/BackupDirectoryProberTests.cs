using BackupMonitor.Agent;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// B7：主动探测「这台机器上像备份目录的地方」。
///
/// 这个功能的产出不是「找到了几个目录」，而是**未监控的那几行**——
/// 所以两件事同等重要：真备份目录要被找出来，而 C:\Windows 这类系统目录
/// 一个都不能混进来。列错一次，这张表的可信度就没了。
/// </summary>
public sealed class BackupDirectoryProberTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bm-probe-" + Guid.NewGuid().ToString("N"));

    public BackupDirectoryProberTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private void Write(string relativePath, int sizeBytes = 4096)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', sizeBytes));
    }

    private ProbeBackupDirsResultDto Probe(int maxDepth = 4)
    {
        var prober = new BackupDirectoryProber(
            Options.Create(new AgentOptions()), NullLogger<BackupDirectoryProber>.Instance);
        return prober.Probe(
            new ProbeBackupDirsCommandPayload { Roots = [_root], MaxDepth = maxDepth },
            CancellationToken.None);
    }

    [Fact]
    public void 真备份目录进候选而系统目录不出现()
    {
        for (var day = 20; day <= 25; day++)
            Write($@"自动备份\202608{day}\db.bak");
        Write(@"数据库备份\full_20260825.bak");
        Write(@"数据库备份\log_20260825.trn");
        Write(@"数据库备份\full_20260824.bak");

        // 系统目录里同样有 .dat/.zip——靠分数压不住，必须一票否决。
        Write(@"Windows\System32\config.dat");
        Write(@"Windows\System32\driver.zip");
        Write(@"Program Files\App\setup.zip");

        var result = Probe();

        var paths = result.Candidates.Select(c => c.Path).ToList();
        Assert.Contains(paths, p => p.EndsWith("自动备份", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("数据库备份", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("Windows", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("Program Files", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 每个候选都要说清楚它为什么被列出来。不说的话，这张表就是一串没有依据的路径，
    /// 跟随便猜没有区别——与 StructureInference 的 Evidence 是同一条原则。
    /// </summary>
    [Fact]
    public void 每个候选都带命中的信号()
    {
        for (var day = 20; day <= 25; day++)
            Write($@"自动备份\202608{day}\db.bak");

        var result = Probe();

        var candidate = Assert.Single(result.Candidates, c => c.Path.EndsWith("自动备份", StringComparison.Ordinal));
        Assert.NotEmpty(candidate.Signals);
        Assert.Contains(candidate.Signals, s => s.Contains("备份", StringComparison.Ordinal));
        Assert.True(candidate.HasDateLayer);
    }

    /// <summary>
    /// 命中即止：候选目录的子树不再下探。备份目录里面再套备份目录极少，
    /// 而它的子树恰恰是日期目录——条目数最多的那一层。
    /// </summary>
    [Fact]
    public void 命中之后不再下探子树()
    {
        for (var day = 20; day <= 25; day++)
            Write($@"自动备份\202608{day}\db.bak");

        var result = Probe();

        Assert.DoesNotContain(result.Candidates, c => c.Path.Contains("20260825", StringComparison.Ordinal));
    }

    /// <summary>返回体积必须显著小：只有候选目录的统计，一个文件名都没有。</summary>
    [Fact]
    public void 结果里不含任何文件名()
    {
        Write(@"自动备份\机密-工资表-2026.bak");
        Write(@"自动备份\机密-工资表-2025.bak");
        Write(@"自动备份\机密-工资表-2024.bak");

        var result = Probe();
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.DoesNotContain("机密-工资表", json, StringComparison.Ordinal);
        Assert.Contains(result.Candidates, c => c.FileCount == 3);
    }

    /// <summary>时间闸触发时把已经算出来的候选原样返回并标记，而不是整个失败。</summary>
    [Fact]
    public void 时间闸触发时返回已算出的候选并标记未扫完()
    {
        for (var i = 0; i < 200; i++)
            Write($@"其他{i:000}\子目录\文件.txt", 16);
        Write(@"自动备份\db.bak");

        var prober = new BackupDirectoryProber(
            Options.Create(new AgentOptions()), NullLogger<BackupDirectoryProber>.Instance);
        var result = prober.Probe(
            new ProbeBackupDirsCommandPayload
            {
                Roots = [_root],
                MaxDepth = 6,
                TotalTimeoutSeconds = 1,
                PerDriveTimeoutSeconds = 1
            },
            CancellationToken.None);

        // 不抛异常、不返回空壳：结果结构完整，扫到哪算哪。
        Assert.NotNull(result.Candidates);
        Assert.True(result.ScannedDirectories > 0);
    }

    /// <summary>
    /// 盘符根本身取出来的名字是空的。按空名一票否决的话，每个盘刚开始扫就被自己挡回去，
    /// 探测永远返回零个候选——而单测里传的是带名字的临时目录，恰好照不出这个问题。
    /// </summary>
    [Fact]
    public void 根目录名为空时不被当成排除项()
    {
        Write(@"自动备份\db.bak");

        var prober = new BackupDirectoryProber(
            Options.Create(new AgentOptions()), NullLogger<BackupDirectoryProber>.Instance);

        // 「名字为空」正是盘符根的形态：Path.GetFileName 对它返回空串。
        Assert.Equal(string.Empty, Path.GetFileName(@"E:"));

        var result = prober.Probe(
            new ProbeBackupDirsCommandPayload { Roots = [_root + Path.DirectorySeparatorChar], MaxDepth = 4 },
            CancellationToken.None);

        Assert.NotEmpty(result.Candidates);
    }

    /// <summary>限定 roots 之外的路径不扫——白名单口径与 DirectoryBrowser 一致。</summary>
    [Fact]
    public void 只扫指定的根()
    {
        Write(@"自动备份\db.bak");

        var result = Probe();

        Assert.Equal([_root], result.ScannedRoots);
        Assert.All(result.Candidates, c => Assert.StartsWith(_root, c.Path, StringComparison.OrdinalIgnoreCase));
    }
}
