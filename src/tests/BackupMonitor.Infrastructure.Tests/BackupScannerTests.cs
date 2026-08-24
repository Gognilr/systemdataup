using BackupMonitor.Agent;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 识别器决定「源目录里的哪些文件算一份备份」。判错的后果不对称：
/// 多收几个无关文件只是清单变长，而漏掉本该纳入的文件会让系统报「没有备份」——
/// 一个备份监控系统给出的最危险的错误答案。
/// </summary>
public sealed class BackupScannerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bm-scan-" + Guid.NewGuid().ToString("N"));

    public BackupScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }

    private void WriteFile(string name, string content = "backup-payload")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<BackupScanResult> ScanAsync(string recognizerType, string recognizerConfig)
    {
        var scanner = new BackupScanner(NullLogger<BackupScanner>.Instance);
        var results = await scanner.ScanAsync(new AgentTaskConfigDto
        {
            TaskId = Guid.NewGuid(),
            Name = "扫描测试",
            ApplicationName = "SQLServer",
            SourcePath = _root,
            RecognizerType = recognizerType,
            TaskMode = "automatic",
            Enabled = true,
            RecognizerConfig = recognizerConfig,
            // 稳定观察窗口设为 0：测试刚写完文件，否则一律返回 still_changing。
            StabilityIntervalSeconds = 0
        }, CancellationToken.None);

        return Assert.Single(results);
    }

    /// <summary>
    /// 「三个文件齐了才算一次完整备份」——multi_file_set + requiredFiles 的典型用法。
    /// </summary>
    [Fact]
    public async Task 三个必需文件齐全时预检通过()
    {
        WriteFile("full.bak");
        WriteFile("full.trn");
        WriteFile("full.ctl");

        var result = await ScanAsync(
            "multi_file_set",
            """{"includePatterns":["*.bak","*.trn","*.ctl"],"requiredFiles":["*.bak","*.trn","*.ctl"]}""");

        Assert.Equal("passed", result.Status);
        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, file => Assert.True(file.IsRequired));
    }

    /// <summary>缺一个就不能算一份完整备份，必须报出来而不是当作通过。</summary>
    [Fact]
    public async Task 缺少任一必需文件时预检失败()
    {
        WriteFile("full.bak");
        WriteFile("full.trn");

        var result = await ScanAsync(
            "multi_file_set",
            """{"includePatterns":["*.bak","*.trn","*.ctl"],"requiredFiles":["*.bak","*.trn","*.ctl"]}""");

        Assert.Equal("required_file_missing", result.Status);
        Assert.Equal("REQUIRED_FILE_MISSING", result.FailureCode);
    }

    /// <summary>
    /// SQL Server 的 BACKUP DATABASE 默认产出 .bak。
    /// 这个扩展名一度被当作「编辑器留下的副本」无条件滤掉，
    /// 结果是最常见的备份格式在系统里完全不可见，且只报「未发现符合识别规则的备份文件」。
    /// </summary>
    [Fact]
    public async Task 不带任何规则时也能识别bak文件()
    {
        WriteFile("finance_full_20260824.bak");

        var result = await ScanAsync("multi_file_set", "{}");

        Assert.Equal("passed", result.Status);
        Assert.Equal("finance_full_20260824.bak", Assert.Single(result.Files).RelativePath);
    }

    /// <summary>半成品文件仍然要默认滤掉，否则会把正在写入的文件当成备份。</summary>
    [Fact]
    public async Task 默认滤掉临时文件()
    {
        WriteFile("full.bak");
        WriteFile("full.tmp");
        WriteFile("full.partial");
        WriteFile("~full.bak");

        var result = await ScanAsync("multi_file_set", "{}");

        Assert.Equal("passed", result.Status);
        Assert.Equal("full.bak", Assert.Single(result.Files).RelativePath);
    }

    /// <summary>
    /// 管理员点名要的文件不该再被启发式规则否决——
    /// 有些备份产品确实会把成品命名成 .tmp，这时系统没有资格替他判断。
    /// </summary>
    [Fact]
    public async Task 显式包含的临时扩展名不再被滤掉()
    {
        WriteFile("export.tmp");

        var result = await ScanAsync("multi_file_set", """{"includePatterns":["*.tmp"]}""");

        Assert.Equal("passed", result.Status);
        Assert.Equal("export.tmp", Assert.Single(result.Files).RelativePath);
    }

    /// <summary>显式排除优先于一切，包括显式包含。</summary>
    [Fact]
    public async Task 显式排除优先于显式包含()
    {
        WriteFile("keep.bak");
        WriteFile("scratch.bak");

        var result = await ScanAsync(
            "multi_file_set",
            """{"includePatterns":["*.bak"],"excludePatterns":["scratch.*"]}""");

        Assert.Equal("passed", result.Status);
        Assert.Equal("keep.bak", Assert.Single(result.Files).RelativePath);
    }

    /// <summary>源目录不存在时要给出可定位的失败原因，而不是「没有备份」。</summary>
    [Fact]
    public async Task 源目录不存在时报路径错误()
    {
        var scanner = new BackupScanner(NullLogger<BackupScanner>.Instance);
        var results = await scanner.ScanAsync(new AgentTaskConfigDto
        {
            TaskId = Guid.NewGuid(),
            Name = "路径错误",
            ApplicationName = "SQLServer",
            SourcePath = Path.Combine(_root, "does-not-exist"),
            RecognizerType = "multi_file_set",
            TaskMode = "automatic",
            Enabled = true,
            RecognizerConfig = "{}"
        }, CancellationToken.None);

        Assert.Equal("path_not_found", Assert.Single(results).Status);
    }
}
