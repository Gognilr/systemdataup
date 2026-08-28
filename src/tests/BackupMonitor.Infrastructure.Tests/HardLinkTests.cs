using BackupMonitor.Infrastructure.Common;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 入库阶段的硬链接（待办方案 H）。
///
/// 6GB 备份集原本是「6GB 读 + 6GB 写」地复制进仓库；同卷 NTFS 时改建硬链接，
/// 入库耗时降到一次元数据操作。这里守住三件事：真的能建、共享同一份数据、
/// 走不通时安静地返回 false 而不是抛异常——它是优化路径，不能成为入库失败的原因。
/// </summary>
public class HardLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bm-hardlink-tests", Guid.NewGuid().ToString("N"));

    public HardLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 同卷建链接后删掉原名字数据仍在()
    {
        if (!OperatingSystem.IsWindows())
            return;   // 硬链接实现只在 Windows 上生效，其他平台走复制路径

        var staged = Path.Combine(_root, "staged.part");
        var repository = Path.Combine(_root, "repo.bin");
        File.WriteAllText(staged, "backup-payload");

        Assert.True(HardLink.TryCreate(staged, repository));
        Assert.Equal("backup-payload", File.ReadAllText(repository));

        // 暂存清理删掉的只是「另一个名字」，仓库里的数据不受影响——
        // 这正是入库之后 LifecycleExpiryWorker 会做的事。
        File.Delete(staged);
        Assert.True(File.Exists(repository));
        Assert.Equal("backup-payload", File.ReadAllText(repository));
    }

    [Fact]
    public void 目标已存在时返回false交给复制路径()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var staged = Path.Combine(_root, "staged2.part");
        var repository = Path.Combine(_root, "exists.bin");
        File.WriteAllText(staged, "a");
        File.WriteAllText(repository, "b");

        Assert.False(HardLink.TryCreate(staged, repository));
        Assert.Equal("b", File.ReadAllText(repository));   // 不覆盖既有文件
    }

    [Fact]
    public void 源文件不存在时不抛异常()
    {
        var missing = Path.Combine(_root, "missing.part");
        var target = Path.Combine(_root, "target.bin");

        Assert.False(HardLink.TryCreate(missing, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void 跨卷直接返回false()
    {
        // 盘符不同即跨卷，不必发一次注定失败的系统调用。
        // 用一个几乎不可能存在的盘符，保证判断只落在「根不同」这一条上。
        Assert.False(HardLink.TryCreate(@"Q:\staging\a.part", Path.Combine(_root, "b.bin")));
    }
}
