using BackupMonitor.Infrastructure.Common;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 路径安全单元测试（设计书 23.4 路径安全 / DEV-PROMPTS 提示词 3c）：
/// 覆盖 ..、UNC、\\?\、盘符、尾随空格与点、大小写穿越、超长路径。
/// 纯逻辑测试，不依赖数据库。
/// </summary>
public class PathSafetyTests
{
    // ---------- IsValidRelativePath：合法路径 ----------

    [Theory]
    [InlineData("a/b/c.bak")]
    [InlineData("data_2026/u8_backup.rar")]
    [InlineData("file.bak")]
    [InlineData("a/b..c/d.txt")]      // 段内含 ".." 但段本身不是 ".."，合法
    [InlineData("backup/2026/08/07/db.tar.gz")]
    public void 合法相对路径应通过(string path)
        => Assert.True(PathSafety.IsValidRelativePath(path), path);

    // ---------- IsValidRelativePath：非法路径 ----------

    [Theory]
    [InlineData("../x")]                          // 头部 ..
    [InlineData("..")]                            // 纯 ..
    [InlineData("a/../../etc/passwd")]            // 段级 .. 穿越
    [InlineData("a/./b")]                         // 段级 .
    [InlineData("/abs/path")]                     // 绝对路径
    [InlineData("a//b")]                          // 空段
    [InlineData(@"\\server\share\file")]          // UNC
    [InlineData(@"\\.\PhysicalDrive0")]           // 设备路径
    [InlineData(@"\\?\C:\Windows")]               // 设备路径（长路径前缀）
    [InlineData(@"\\?\UNC\server\share")]         // 设备路径（UNC 变体）
    [InlineData(@"C:\abs")]                       // 盘符绝对路径
    [InlineData("C:x")]                           // 盘符相对路径
    [InlineData("a/b ")]                          // 段尾随空格
    [InlineData("a/b.")]                          // 段尾随点（NTFS 静默剥离）
    [InlineData("a/b.../c")]                      // 中间段尾随点
    public void 非法路径应拒绝(string path)
        => Assert.False(PathSafety.IsValidRelativePath(path), path);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空值应拒绝(string? path)
        => Assert.False(PathSafety.IsValidRelativePath(path));

    [Fact]
    public void 超长路径应拒绝()
    {
        var path = "a/" + new string('x', 2048);
        Assert.True(path.Length > 2048);
        Assert.False(PathSafety.IsValidRelativePath(path));

        // 边界内（<=2048）仍然合法
        var ok = "a/" + new string('x', 2040);
        Assert.True(ok.Length <= 2048);
        Assert.True(PathSafety.IsValidRelativePath(ok));
    }

    [Fact]
    public void 含操作系统非法字符的段应拒绝()
    {
        // 平台感知：Windows 含 <>:"|?* 等，Linux 仅 '\0' 与 '/'；
        // 分隔符已由规范化步骤处理，这里只验证其余非法字符。
        var invalidChars = Path.GetInvalidFileNameChars()
            .Where(c => c is not ('/' or '\\'))
            .ToArray();
        Assert.NotEmpty(invalidChars);

        foreach (var c in invalidChars)
        {
            var path = $"a/b{c}c.txt";
            Assert.False(PathSafety.IsValidRelativePath(path),
                $"字符 0x{(int)c:X2} 应被拒绝");
        }
    }

    // ---------- ResolveUnderBase ----------

    [Fact]
    public void ResolveUnderBase_穿越应返回null()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "bm_pathsafety_base");
        Assert.Null(PathSafety.ResolveUnderBase(basePath, "../escape.txt"));
        Assert.Null(PathSafety.ResolveUnderBase(basePath, @"..\escape.txt"));
        Assert.Null(PathSafety.ResolveUnderBase(basePath, "a/../../escape.txt"));
    }

    [Fact]
    public void ResolveUnderBase_合法拼接应位于基目录内()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "bm_pathsafety_base");
        var resolved = PathSafety.ResolveUnderBase(basePath, "sub/file.txt");
        Assert.NotNull(resolved);

        var fullBase = Path.GetFullPath(basePath);
        var prefix = fullBase.EndsWith(Path.DirectorySeparatorChar)
            ? fullBase
            : fullBase + Path.DirectorySeparatorChar;
        Assert.StartsWith(prefix, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("file.txt", resolved);
    }

    [Fact]
    public void ResolveUnderBase_大小写不同的基目录不应误拒()
    {
        // OrdinalIgnoreCase 包含判断：Windows 大小写不敏感盘上不能把同目录判成逃逸
        var basePath = Path.Combine(Path.GetTempPath(), "bm_PS_Base");
        Assert.NotNull(PathSafety.ResolveUnderBase(basePath, "f.txt"));
        Assert.NotNull(PathSafety.ResolveUnderBase(basePath.ToUpperInvariant(), "f.txt"));
        Assert.NotNull(PathSafety.ResolveUnderBase(basePath.ToLowerInvariant(), "f.txt"));
    }

    // ---------- IsUnderBase：物理删除围栏的判定谓词 ----------
    // RetentionCleanupWorker 在递归删除前用它确认目标确实落在仓库根之下。
    // 判错的代价是删掉仓库之外的目录，因此边界必须逐条钉死。

    [Theory]
    [InlineData(@"E:\Repo", @"E:\Repo\host\task\2026-08-21_020000")]
    [InlineData(@"E:\Repo\", @"E:\Repo\a")]                 // 基目录带尾随分隔符
    [InlineData(@"e:\repo", @"E:\REPO\a\b")]                // Windows 大小写不敏感
    public void IsUnderBase_基目录之内应通过(string basePath, string fullPath)
        => Assert.True(PathSafety.IsUnderBase(basePath, fullPath));

    [Theory]
    [InlineData(@"E:\Repo", @"E:\Repo")]                    // 基目录自身不算"之内"
    [InlineData(@"E:\Repo", @"E:\Repo2\a")]                 // 同前缀兄弟目录，经典误判点
    [InlineData(@"E:\Repo", @"E:\RepoOld\a")]               // 同上
    [InlineData(@"E:\Repo", @"C:\Program Files\PostgreSQL")]// 完全无关的路径
    [InlineData(@"E:\Repo", @"E:\Repo\..\Windows")]         // 规范化后穿出基目录
    [InlineData(@"E:\Repo", @"E:\")]                        // 盘符根
    public void IsUnderBase_基目录之外必须拒绝(string basePath, string fullPath)
        => Assert.False(PathSafety.IsUnderBase(basePath, fullPath));

    [Theory]
    [InlineData("", @"E:\Repo\a")]
    [InlineData(@"E:\Repo", "")]
    [InlineData("   ", @"E:\Repo\a")]
    public void IsUnderBase_空输入一律拒绝(string basePath, string fullPath)
        => Assert.False(PathSafety.IsUnderBase(basePath, fullPath));

    // ---------- SanitizePathComponent ----------

    [Theory]
    [InlineData("a..b", "a_b")]        // ".." 替换为下划线
    [InlineData("..", "_")]
    [InlineData("name.", "name")]      // 尾随点去除
    [InlineData("name...", "name_")]   // ".." 替换先于尾随点去除（顺序产物，安全）
    [InlineData("  x  ", "x")]         // 首尾空格去除
    [InlineData("", "unnamed")]
    [InlineData("   ", "unnamed")]
    public void SanitizePathComponent_清理行为(string input, string expected)
        => Assert.Equal(expected, PathSafety.SanitizePathComponent(input));

    [Fact]
    public void SanitizePathComponent_超长截断到100()
    {
        var result = PathSafety.SanitizePathComponent(new string('a', 150));
        Assert.Equal(100, result.Length);
    }

    // ---------- Windows 保留设备名（审查 P2-6） ----------

    /// <summary>
    /// CON / NUL / COM1~9 / LPT1~9 在任何目录下都不能作为文件名，带扩展名同样保留
    /// （NUL.txt 依旧指向空设备）。写入会「静默成功」而内容丢失，必须在入口拒绝。
    /// </summary>
    [Theory]
    [InlineData("NUL")]
    [InlineData("nul")]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("NUL.txt")]
    [InlineData("con.bak")]
    [InlineData("data/NUL")]
    [InlineData("data/COM3/file.dat")]
    public void IsValidRelativePath_拒绝Windows保留设备名(string path)
        => Assert.False(PathSafety.IsValidRelativePath(path));

    /// <summary>形近但不保留的名字不得误杀。</summary>
    [Theory]
    [InlineData("CONFIG")]
    [InlineData("console.log")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    [InlineData("nullable.json")]
    [InlineData("data/AUXILIARY/x.dat")]
    public void IsValidRelativePath_形近名不误杀(string path)
        => Assert.True(PathSafety.IsValidRelativePath(path));

    /// <summary>服务端生成路径时，撞上保留名要改写而不是原样使用。</summary>
    [Theory]
    [InlineData("NUL", "_NUL")]
    [InlineData("con", "_con")]
    [InlineData("COM1", "_COM1")]
    [InlineData("CONFIG", "CONFIG")]
    public void SanitizePathComponent_保留设备名改写(string input, string expected)
        => Assert.Equal(expected, PathSafety.SanitizePathComponent(input));
}
