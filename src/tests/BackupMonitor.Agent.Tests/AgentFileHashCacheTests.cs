using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent.Tests;

/// <summary>
/// 文件级哈希缓存（实施方案 T5）。
///
/// 这里验的是**命中判据**，不是性能。判据放宽一点，表现是「源文件换了内容但哈希没重算」——
/// 备份看起来正常、manifest 也自洽，只有真去恢复的那一天才会发现存进去的不是当时那份。
/// 所以四个判据字段每一个都单独验一次失配。
/// </summary>
public class AgentFileHashCacheTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "bm-hashcache-" + Guid.NewGuid().ToString("N"));

    private AgentFileHashCache NewCache() => new(
        Options.Create(new AgentOptions
        {
            DataDirectory = _dataDirectory,
            // 测试环境不套 ACL：临时目录上收紧 ACL 会让清理阶段自己删不掉。
            EnforceAcl = false
        }),
        NullLogger<AgentFileHashCache>.Instance);

    private static (string Path, long Size, DateTime Mtime) WriteFile(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        var info = new FileInfo(path);
        return (path, info.Length, info.LastWriteTimeUtc);
    }

    [Fact]
    public void 四项判据全等时命中()
    {
        var taskId = Guid.NewGuid();
        var f = WriteFile(_dataDirectory, "a.bak", "hello");

        var session = NewCache().Open(taskId);
        session.Put(f.Path, f.Size, f.Mtime, "quick-1", "sha-1");
        session.Save();

        // 重新打开一次，确认走的是落盘的那一份而不是内存里的残留。
        var reopened = NewCache().Open(taskId);
        Assert.Equal("sha-1", reopened.TryGet(f.Path, f.Size, f.Mtime, "quick-1"));
    }

    [Fact]
    public void 大小变了不命中()
    {
        var taskId = Guid.NewGuid();
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        var session = NewCache().Open(taskId);
        session.Put(f.Path, f.Size, f.Mtime, "quick-1", "sha-1");

        Assert.Null(session.TryGet(f.Path, f.Size + 1, f.Mtime, "quick-1"));
    }

    [Fact]
    public void 修改时间变了不命中()
    {
        var taskId = Guid.NewGuid();
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        var session = NewCache().Open(taskId);
        session.Put(f.Path, f.Size, f.Mtime, "quick-1", "sha-1");

        Assert.Null(session.TryGet(f.Path, f.Size, f.Mtime.AddSeconds(1), "quick-1"));
    }

    [Fact]
    public void 头尾采样变了不命中()
    {
        // 这一条是四个判据里最关键的：size 与 mtime 都能被刻意保持不变，
        // 而 QuickHash 覆盖头尾各 64KB。它和既有的单元级快速指纹用的是同一把尺子。
        var taskId = Guid.NewGuid();
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        var session = NewCache().Open(taskId);
        session.Put(f.Path, f.Size, f.Mtime, "quick-1", "sha-1");

        Assert.Null(session.TryGet(f.Path, f.Size, f.Mtime, "quick-2"));
    }

    [Fact]
    public void 采样为空不命中()
    {
        // 缓存里没存 QuickHash 的条目一律判不命中：宁可多读一遍盘，不要放过一个判据。
        var taskId = Guid.NewGuid();
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        var session = NewCache().Open(taskId);
        session.Put(f.Path, f.Size, f.Mtime, quickHash: null, "sha-1");

        Assert.Null(session.TryGet(f.Path, f.Size, f.Mtime, null));
    }

    [Fact]
    public void 路径大小写不敏感()
    {
        var taskId = Guid.NewGuid();
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        var session = NewCache().Open(taskId);
        session.Put(f.Path, f.Size, f.Mtime, "quick-1", "sha-1");

        Assert.Equal("sha-1", session.TryGet(f.Path.ToUpperInvariant(), f.Size, f.Mtime, "quick-1"));
    }

    [Fact]
    public void 不跨任务共享()
    {
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        var cache = NewCache();
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        var a = cache.Open(taskA);
        a.Put(f.Path, f.Size, f.Mtime, "quick-1", "sha-1");
        a.Save();

        Assert.Null(cache.Open(taskB).TryGet(f.Path, f.Size, f.Mtime, "quick-1"));
    }

    [Fact]
    public void 缓存文件损坏时按无缓存处理()
    {
        // 降级方向必须是「全部重算」而不是「抛异常」：缓存是加速手段，
        // 让它把一次预检搞失败是本末倒置。
        var taskId = Guid.NewGuid();
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        var cache = NewCache();
        var session = cache.Open(taskId);
        session.Put(f.Path, f.Size, f.Mtime, "quick-1", "sha-1");
        session.Save();

        var cacheFile = Path.Combine(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(_dataDirectory)),
            "hashcache", $"{taskId:N}.json");
        Assert.True(File.Exists(cacheFile));
        File.WriteAllText(cacheFile, "{ 这不是合法 JSON");

        Assert.Null(NewCache().Open(taskId).TryGet(f.Path, f.Size, f.Mtime, "quick-1"));
    }

    [Fact]
    public void 缓存文件缺失时按无缓存处理()
    {
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        Assert.Null(NewCache().Open(Guid.NewGuid()).TryGet(f.Path, f.Size, f.Mtime, "quick-1"));
    }

    [Fact]
    public void 没有写入时不落盘()
    {
        // 单元级快速指纹命中、整段哈希循环被跳过时，会话里一次 Put 都没有。
        // 那时不该白写一遍文件。
        var taskId = Guid.NewGuid();
        NewCache().Open(taskId).Save();

        var cacheFile = Path.Combine(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(_dataDirectory)),
            "hashcache", $"{taskId:N}.json");
        Assert.False(File.Exists(cacheFile));
    }

    [Fact]
    public void 见过的条目会被保留()
    {
        // Touch 的存在意义：单元级指纹命中时那批文件一次 TryGet 都不走，
        // 没有它就会在保留期后被当成「已删除」淘汰，下一次白白重算一遍全盘。
        var taskId = Guid.NewGuid();
        var f = WriteFile(_dataDirectory, "a.bak", "hello");
        var cache = NewCache();

        var first = cache.Open(taskId);
        first.Put(f.Path, f.Size, f.Mtime, "quick-1", "sha-1");
        first.Save();

        var second = cache.Open(taskId);
        second.Touch(f.Path);
        second.Save();

        Assert.Equal("sha-1", cache.Open(taskId).TryGet(f.Path, f.Size, f.Mtime, "quick-1"));
    }

    public void Dispose()
    {
        try
        {
            var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_dataDirectory));
            if (Directory.Exists(expanded))
                Directory.Delete(expanded, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不该让测试失败。
        }
    }
}
