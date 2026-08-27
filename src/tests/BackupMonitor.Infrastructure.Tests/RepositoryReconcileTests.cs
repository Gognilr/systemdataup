using BackupMonitor.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 仓库孤儿目录对帐的判定规则。
///
/// 这一条要防的失败模式是：入库最后两步（Directory.Move 改名 → SaveChangesAsync 写库）
/// 之间进程被杀，文件留在仓库里而数据库没有记录。界面显示备份失败，
/// 盘上其实已有一份完整备份，而且此前没有任何东西会去发现它。
///
/// 全部风险都在「哪些目录算孤儿」上，那是纯文件系统的判断，所以这组用例
/// 直接对着真实目录树跑，不拉数据库——判错的代价是把人的备份报成垃圾，
/// 或者把真垃圾漏掉让仓库继续被填满。
/// </summary>
public sealed class RepositoryReconcileTests : IDisposable
{
    private readonly string _root;
    private readonly RepositoryReconcileWorker _worker =
        new(null!, NullLogger<RepositoryReconcileWorker>.Instance);

    /// <summary>静默期外的时间点：建好的目录默认都比它旧，除非用例另行指定</summary>
    private readonly DateTime _cutoff = DateTime.UtcNow.AddHours(1);

    public RepositoryReconcileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bm-reconcile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    [Fact]
    public void 有清单但库里查不到的目录被判为孤儿()
    {
        // 崩在 Directory.Move 之后、SaveChangesAsync 之前的现场：
        // 目录已是正式命名，清单也在（清单先于改名落盘），只差数据库那一行。
        var orphan = MakeBackupSetDir("host-a", "任务甲", "2026-08-27_030000", withManifest: true);

        var found = _worker.ScanOrphans(_root, Known(), _cutoff);

        var one = Assert.Single(found);
        Assert.Equal(orphan, one.Path);
        Assert.Equal(OrphanKind.Missing, one.Kind);
        Assert.True(one.SizeBytes > 0, "孤儿目录的占用大小要报出来，否则人无从判断值不值得处理");
    }

    [Fact]
    public void 库里有记录的目录不被判为孤儿()
    {
        var known = MakeBackupSetDir("host-a", "任务甲", "2026-08-27_030000", withManifest: true);

        Assert.Empty(_worker.ScanOrphans(_root, Known(known), _cutoff));
    }

    [Fact]
    public void 路径写法不同不影响比对()
    {
        var dir = MakeBackupSetDir("host-a", "任务甲", "2026-08-27_030000", withManifest: true);

        // 库里存的是入库当时拼出来的字符串，可能带尾部分隔符或 .\ 片段。
        // 不规范化就比，会把好端端的备份集报成孤儿——这种误报最伤人，
        // 因为它指向的目录里确实躺着一份真备份。
        var known = Known(dir + Path.DirectorySeparatorChar);

        Assert.Empty(_worker.ScanOrphans(_root, known, _cutoff));
    }

    [Fact]
    public void 提交临时目录被判为孤儿()
    {
        // 崩在复制文件或写清单过程中的现场：目录还叫 .commit-xxxxxxxx，
        // 正常情况下它的寿命只有一次 Directory.Move 那么长。
        var temp = Path.Combine(_root, "host-a", "任务甲", "2026-08-27_030000.commit-a1b2c3d4");
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, "part.bak"), "半份数据");

        var one = Assert.Single(_worker.ScanOrphans(_root, Known(), _cutoff));
        Assert.Equal(temp, one.Path);
        Assert.Equal(OrphanKind.CommitTemp, one.Kind);
    }

    [Fact]
    public void 静默期内的目录不报()
    {
        // 正在进行的入库本来就是「目录已建、库里无行」的样子。
        // 静默期不够宽，每轮巡检都会把正在干活的目录报成残骸。
        MakeBackupSetDir("host-a", "任务甲", "2026-08-27_030000", withManifest: true);
        Directory.CreateDirectory(Path.Combine(_root, "host-a", "任务甲", "2026-08-27_030000.commit-a1b2c3d4"));

        var cutoff = DateTime.UtcNow.AddHours(-24);   // 刚建的目录都比它新

        Assert.Empty(_worker.ScanOrphans(_root, Known(), cutoff));
    }

    [Fact]
    public void 中间层目录和备份集内部子目录都不被误报()
    {
        // host-a 和 任务甲 是仓库结构的中间层，它们没有清单也不该被报；
        // 备份集内部的 sub/ 是备份内容本身，更不该被单独报成一个孤儿。
        var known = MakeBackupSetDir("host-a", "任务甲", "2026-08-27_030000", withManifest: true);
        var inner = Path.Combine(known, "sub", "deeper");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "data.bak"), "备份内容");

        Assert.Empty(_worker.ScanOrphans(_root, Known(known), _cutoff));
    }

    [Fact]
    public void 没有清单的空目录不被判为孤儿()
    {
        // 手工建的、或者删了一半剩下的空壳目录，没有清单就没有证据说它是一份备份。
        // 这里刻意选择漏报而不是误报：报出来会让人以为盘上有份备份可以救。
        Directory.CreateDirectory(Path.Combine(_root, "host-a", "任务甲", "2026-08-27_030000"));

        Assert.Empty(_worker.ScanOrphans(_root, Known(), _cutoff));
    }

    [Fact]
    public void 业务单元那一层也能扫到()
    {
        // 仓库布局有两种深度：主机/任务/版本，和主机/任务/业务单元/版本。
        // 只按固定深度找会漏掉带业务单元的那一半。
        var orphan = Path.Combine(_root, "host-a", "任务甲", "财务部", "2026-08-27_030000");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "manifest.json"), "{}");

        var one = Assert.Single(_worker.ScanOrphans(_root, Known(), _cutoff));
        Assert.Equal(orphan, one.Path);
    }

    // ---------- 基础设施 ----------

    private string MakeBackupSetDir(string host, string task, string version, bool withManifest)
    {
        var dir = Path.Combine(_root, host, task, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "data.bak"), "备份内容");
        if (withManifest)
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
        return dir;
    }

    private static HashSet<string> Known(params string[] paths) =>
        new(paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))),
            StringComparer.OrdinalIgnoreCase);
}
