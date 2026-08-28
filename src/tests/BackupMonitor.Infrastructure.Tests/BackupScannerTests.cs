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

    private async Task<BackupScanResult> ScanAsync(string recognizerType, string recognizerConfig) =>
        Assert.Single(await ScanAllAsync(recognizerType, recognizerConfig));

    private async Task<BackupScanResult> ScanForTestAsync(string recognizerType, string recognizerConfig) =>
        Assert.Single(await ScanAllAsync(recognizerType, recognizerConfig, testOnly: true));

    private async Task<List<BackupScanResult>> ScanAllAsync(string recognizerType, string recognizerConfig, bool testOnly = false)
    {
        var scanner = new BackupScanner(NullLogger<BackupScanner>.Instance);
        var task = TaskConfig(recognizerType, recognizerConfig);
        return testOnly
            ? await scanner.ScanForTestAsync(task, CancellationToken.None)
            : await scanner.ScanAsync(task, CancellationToken.None);
    }

    private AgentTaskConfigDto TaskConfig(string recognizerType, string recognizerConfig) => new()
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
    };

    /// <summary>
    /// 识别测试（试扫）不算 SHA-256。
    ///
    /// 这不是性能优化的锦上添花：识别测试的等待窗口只有一分钟，而全量哈希要把每个文件
    /// 整读一遍——几 GB 的备份光哈希就能吃掉整个窗口，界面除了「等待超时」什么都给不出。
    /// 哈希在这条路径上没有任何用处：界面只显示文件名、大小、是否必需。
    /// </summary>
    [Fact]
    public async Task 试扫不计算哈希()
    {
        WriteFile("full.bak");

        var result = await ScanForTestAsync("multi_file_set", "{}");

        Assert.Equal("passed", result.Status);
        var file = Assert.Single(result.Files);
        Assert.Equal("full.bak", file.RelativePath);
        Assert.Null(file.Sha256);
        Assert.Null(file.QuickHash);
    }

    /// <summary>
    /// 试扫的结果不能长得像一份能入库的候选：manifestHash / candidateKey 一律留空，
    /// 且带着 IsTestScan 标记——上报路径据此当场拒绝，而不是产生一个哈希为空的候选备份集。
    /// </summary>
    [Fact]
    public async Task 试扫结果不带候选标识()
    {
        WriteFile("full.bak");

        var result = await ScanForTestAsync("multi_file_set", "{}");

        Assert.True(result.IsTestScan);
        Assert.Null(result.ManifestHash);
        Assert.Null(result.QuickFingerprint);
        Assert.Equal(string.Empty, result.CandidateKey);
    }

    /// <summary>
    /// 试扫和正式扫描必须挑中同一批文件——否则「看看会备份哪些文件」这个功能就是在撒谎。
    /// 两者共用同一套遍历与识别逻辑，这条测试钉的是「以后也别拆开」。
    /// </summary>
    [Fact]
    public async Task 试扫与正式扫描识别出同样的文件()
    {
        WriteFile("full.bak");
        WriteFile("full.trn");
        WriteFile("full.tmp");           // 默认应被滤掉的半成品

        var real = await ScanAsync("multi_file_set", "{}");
        var test = await ScanForTestAsync("multi_file_set", "{}");

        Assert.Equal(real.Status, test.Status);
        Assert.Equal(
            real.Files.Select(f => f.RelativePath).OrderBy(p => p),
            test.Files.Select(f => f.RelativePath).OrderBy(p => p));
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

    /// <summary>
    /// 用友 U8 的真实结构：一个总目录下若干账套，每个账套下每天一个备份目录。
    /// 账套是业务单元，每天的目录是该单元的一个版本。
    ///
    /// 没有 unitLayout 时，账套目录下所有天的文件会被混成一份候选备份集——
    /// manifest 每天变一次、体积不断累加，永远不代表"某一天的那份备份"。
    /// </summary>
    [Fact]
    public async Task 账套下每天一个目录时只取最新那天()
    {
        WriteFile(Path.Combine("账套001", "20260822", "full.bak"));
        WriteFile(Path.Combine("账套001", "20260824", "full.bak"));
        WriteFile(Path.Combine("账套002", "20260823", "full.bak"));
        WriteFile(Path.Combine("账套002", "20260824", "full.bak"));

        // 让"最新"有确定答案：判定依据是目录内最新文件的修改时间。
        File.SetLastWriteTimeUtc(Path.Combine(_root, "账套001", "20260822", "full.bak"), new DateTime(2026, 8, 22, 2, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "账套001", "20260824", "full.bak"), new DateTime(2026, 8, 24, 2, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "账套002", "20260823", "full.bak"), new DateTime(2026, 8, 23, 2, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "账套002", "20260824", "full.bak"), new DateTime(2026, 8, 24, 3, 0, 0, DateTimeKind.Utc));

        var results = await ScanAllAsync(
            "subdirectory_units",
            """{"businessUnitDepth":1,"unitLayout":"latest_directory"}""");

        // 两个账套各产出一份，而不是把所有天混成一份。
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("passed", r.Status));
        Assert.All(results, r => Assert.Single(r.Files));
        Assert.Contains(results, r => r.SourceRoot.EndsWith(Path.Combine("账套001", "20260824"), StringComparison.Ordinal));
        Assert.Contains(results, r => r.SourceRoot.EndsWith(Path.Combine("账套002", "20260824"), StringComparison.Ordinal));

        // 业务单元是账套，不是日期目录。
        Assert.Equal(
            new[] { "账套001", "账套002" },
            results.Select(r => r.BusinessUnit!.ExternalKey).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// depth≥2 时叶子目录名会重复：账套001/20260824 与 账套002/20260824。
    /// ExternalKey 曾经只取叶子名，而业务单元的唯一键是 (task_id, external_key)，
    /// 于是两个账套在服务端被合并成同一个业务单元——一个账套的备份会覆盖另一个的记录。
    /// </summary>
    [Fact]
    public async Task 同名日期目录在不同账套下不能撞键()
    {
        WriteFile(Path.Combine("账套001", "20260824", "full.bak"));
        WriteFile(Path.Combine("账套002", "20260824", "full.bak"));

        var results = await ScanAllAsync("subdirectory_units", """{"businessUnitDepth":2}""");

        Assert.Equal(2, results.Count);
        var keys = results.Select(r => r.BusinessUnit!.ExternalKey).ToList();
        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.Contains('/', key));
    }

    // ---------- 源路径通配 ----------

    private async Task<List<BackupScanResult>> ScanPathAsync(string sourcePath, string recognizerType, string recognizerConfig)
    {
        var scanner = new BackupScanner(NullLogger<BackupScanner>.Instance);
        return await scanner.ScanAsync(new AgentTaskConfigDto
        {
            TaskId = Guid.NewGuid(),
            Name = "通配测试",
            ApplicationName = "Seeyon",
            SourcePath = sourcePath,
            RecognizerType = recognizerType,
            TaskMode = "automatic",
            Enabled = true,
            RecognizerConfig = recognizerConfig,
            StabilityIntervalSeconds = 0
        }, CancellationToken.None);
    }

    /// <summary>root\2025\12、root\2026\07（空）、root\2026\08（有文件），root\*\* 应当扫到 2026\08。</summary>
    [Fact]
    public async Task 通配源路径逐段取最新()
    {
        WriteFile(Path.Combine("2025", "12", "old.bak"));
        WriteFile(Path.Combine("2026", "08", "new.bak"));
        Directory.CreateDirectory(Path.Combine(_root, "2026", "07"));

        File.SetLastWriteTimeUtc(Path.Combine(_root, "2025", "12", "old.bak"), new DateTime(2025, 12, 31, 2, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "2026", "08", "new.bak"), new DateTime(2026, 8, 20, 2, 0, 0, DateTimeKind.Utc));

        var result = Assert.Single(await ScanPathAsync(
            Path.Combine(_root, "*", "*"), "multi_file_set", """{"recursive":false}"""));

        Assert.Equal("passed", result.Status);
        Assert.Equal(Path.Combine(_root, "2026", "08"), result.SourceRoot);
        Assert.Equal("new.bak", Assert.Single(result.Files).RelativePath);
    }

    /// <summary>
    /// 空的 2026\07 不能被选中。它的目录时间戳比 08 新（后建的），
    /// 只有按"目录内最新文件时间"判定才会把它排到后面——很多备份程序先建目录
    /// 再慢慢往里写，目录自身的时间没有参考价值。
    /// </summary>
    [Fact]
    public async Task 通配展开不选空目录()
    {
        WriteFile(Path.Combine("2026", "08", "new.bak"));
        var empty = Path.Combine(_root, "2026", "09");
        Directory.CreateDirectory(empty);

        File.SetLastWriteTimeUtc(Path.Combine(_root, "2026", "08", "new.bak"), new DateTime(2026, 8, 20, 2, 0, 0, DateTimeKind.Utc));
        // 让空目录的目录时间戳明显更新，确保断言的是"按内容时间"而不是碰巧。
        Directory.SetLastWriteTimeUtc(empty, DateTime.UtcNow);

        var result = Assert.Single(await ScanPathAsync(
            Path.Combine(_root, "*", "*"), "multi_file_set", """{"recursive":false}"""));

        Assert.Equal(Path.Combine(_root, "2026", "08"), result.SourceRoot);
    }

    /// <summary>
    /// 通配一个都没匹配上时，失败信息必须说清楚是通配没匹配上，
    /// 而不是让人以为路径写错了跑去改一条本来正确的配置。
    /// </summary>
    [Fact]
    public async Task 通配无匹配时报路径未找到且说明原因()
    {
        WriteFile(Path.Combine("2026", "08", "new.bak"));

        var result = Assert.Single(await ScanPathAsync(
            Path.Combine(_root, "9999", "*"), "multi_file_set", "{}"));

        Assert.Equal("path_not_found", result.Status);
        Assert.Contains("通配", result.FailureMessage);
        Assert.Contains("不是路径写错了", result.FailureMessage);
    }

    /// <summary>穿越必须被拒绝：展开只会把 * 换成真实目录名，唯一的穿越来源是人写进来的 ..。</summary>
    [Fact]
    public async Task 通配路径里的父目录段被拒绝()
    {
        WriteFile(Path.Combine("2026", "08", "new.bak"));

        var result = Assert.Single(await ScanPathAsync(
            Path.Combine(_root, "..", "*"), "multi_file_set", "{}"));

        Assert.Equal("path_not_found", result.Status);
        Assert.Contains("..", result.FailureMessage);
    }

    /// <summary>通配只允许出现在目录段。盘符段带 * 是完全不同的一件事，必须明确拒绝而不是猜。</summary>
    [Fact]
    public async Task 盘符段的通配被拒绝()
    {
        var result = Assert.Single(await ScanPathAsync(
            @"*:\Backup\2026", "multi_file_set", "{}"));

        Assert.Equal("path_not_found", result.Status);
        Assert.Contains("盘符", result.FailureMessage);
    }

    /// <summary>不带通配的源路径行为与从前完全一致——展开这条路径压根不会被走到。</summary>
    [Fact]
    public async Task 不带通配的源路径不受影响()
    {
        WriteFile("full.bak");

        var result = Assert.Single(await ScanPathAsync(_root, "multi_file_set", "{}"));

        Assert.Equal("passed", result.Status);
        Assert.Equal(_root, result.SourceRoot);
    }
    /// <summary>相对路径没有可当围栏的字面前缀，必须给一条看得懂的拒绝理由。</summary>
    [Fact]
    public async Task 相对通配路径被拒绝并说明要绝对路径()
    {
        var result = Assert.Single(await ScanPathAsync(
            Path.Combine("relative", "*"), "multi_file_set", "{}"));

        Assert.Equal("path_not_found", result.Status);
        Assert.Contains("绝对路径", result.FailureMessage);
    }
    // ---------- groupBy：一次备份 = 同目录下的一组文件 ----------

    /// <summary>
    /// 致远 OA：08 目录里躺着一个月约 30 组 NAME.zip + NAME.properties。
    ///
    /// 不分组的话这 30 天会被算成"一份"：manifest 每天变一次、candidateKey 跟着变，
    /// 永远不代表某一天的那份备份；而且每次扫描要把 30 个 zip 全读一遍算 SHA-256。
    /// </summary>
    [Fact]
    public async Task 按文件名分组时只取最新的一组()
    {
        for (var day = 1; day <= 30; day++)
        {
            var stem = $"2026-08-{day:00}@02_00";
            WriteFile($"{stem}.zip");
            WriteFile($"{stem}.properties");
            var stamp = new DateTime(2026, 8, day, 2, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(_root, $"{stem}.zip"), stamp);
            File.SetLastWriteTimeUtc(Path.Combine(_root, $"{stem}.properties"), stamp.AddSeconds(5));
        }

        var result = await ScanAsync("multi_file_set", """{"recursive":false,"groupBy":"basename"}""");

        Assert.Equal("passed", result.Status);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(
            ["2026-08-30@02_00.properties", "2026-08-30@02_00.zip"],
            result.Files.Select(f => f.RelativePath).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // 业务时间是这一组的最新时间，而不是整个目录的最新时间——这里两者恰好相同，
        // 但下面那个用例证明它确实跟着组走。
        Assert.Equal(new DateTime(2026, 8, 30, 2, 0, 5, DateTimeKind.Utc), result.BackupBusinessTime);
    }

    /// <summary>
    /// 业务时间必须是被选中那一组的最新时间。
    /// 目录里另有一个更旧的组、但它的某个文件时间戳被改得很新时，
    /// 取"整个目录最新"会得出一个不属于这份备份的时间。
    /// </summary>
    [Fact]
    public async Task 业务时间取所选组的最新时间而不是整个目录的()
    {
        WriteFile("old.zip");
        WriteFile("old.properties");
        WriteFile("new.zip");
        WriteFile("new.properties");

        var oldTime = new DateTime(2026, 8, 1, 2, 0, 0, DateTimeKind.Utc);
        var newTime = new DateTime(2026, 8, 20, 2, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "old.zip"), oldTime);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "old.properties"), oldTime);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "new.zip"), newTime);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "new.properties"), newTime.AddMinutes(1));

        var result = await ScanAsync("multi_file_set", """{"recursive":false,"groupBy":"basename"}""");

        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, f => Assert.StartsWith("new.", f.RelativePath, StringComparison.Ordinal));
        Assert.Equal(newTime.AddMinutes(1), result.BackupBusinessTime);
    }

    /// <summary>groupBy 也可以写正则，用第一个捕获组当分组键。</summary>
    [Fact]
    public async Task 正则形式的分组键按捕获组归组()
    {
        foreach (var day in new[] { "2026-08-18", "2026-08-19" })
        {
            WriteFile($"{day}_full.bak");
            WriteFile($"{day}_log.trn");
            var stamp = DateTime.Parse(day + "T02:00:00Z").ToUniversalTime();
            File.SetLastWriteTimeUtc(Path.Combine(_root, $"{day}_full.bak"), stamp);
            File.SetLastWriteTimeUtc(Path.Combine(_root, $"{day}_log.trn"), stamp);
        }

        var result = await ScanAsync(
            "multi_file_set",
            """{"recursive":false,"groupBy":"^(\\d{4}-\\d{2}-\\d{2})"}""");

        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, f => Assert.StartsWith("2026-08-19", f.RelativePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// 编译不过的正则不能抛异常，也不能让备份凭空消失——退化为不分组。
    /// 沿用 RegexMatches 已有的异常吞掉策略：一条写错的 groupBy 的代价应当是
    /// "分组没生效"，而不是"这台机器再也没有备份了"。
    /// </summary>
    [Fact]
    public async Task 非法正则的分组键退化为不分组()
    {
        WriteFile("a.bak");
        WriteFile("b.bak");

        var result = await ScanAsync("multi_file_set", """{"recursive":false,"groupBy":"^([unclosed"}""");

        Assert.Equal("passed", result.Status);
        Assert.Equal(2, result.Files.Count);
    }

    /// <summary>不写 groupBy 时行为与从前完全一致——这是"老任务零影响"的证明。</summary>
    [Fact]
    public async Task 不写分组键时收集整个目录()
    {
        WriteFile("2026-08-18@02_00.zip");
        WriteFile("2026-08-18@02_00.properties");
        WriteFile("2026-08-19@02_00.zip");
        WriteFile("2026-08-19@02_00.properties");

        var result = await ScanAsync("multi_file_set", """{"recursive":false}""");

        Assert.Equal(4, result.Files.Count);
    }

    /// <summary>分组后为空同样要走 no_new_backup，不能拿空集合往下算 manifest。</summary>
    [Fact]
    public async Task 分组前就没有匹配文件时报没有新备份()
    {
        WriteFile("readme.tmp");

        var result = await ScanAsync("multi_file_set", """{"recursive":false,"groupBy":"basename"}""");

        Assert.Equal("no_new_backup", result.Status);
    }
    /// <summary>账套下还没有任何日期目录时，要报"没有备份"而不是让这个账套整个消失。</summary>
    [Fact]
    public async Task 账套下没有备份目录时仍然报告该账套()
    {
        Directory.CreateDirectory(Path.Combine(_root, "账套003"));
        WriteFile(Path.Combine("账套001", "20260824", "full.bak"));

        var results = await ScanAllAsync(
            "subdirectory_units",
            """{"businessUnitDepth":1,"unitLayout":"latest_directory"}""");

        Assert.Equal(2, results.Count);
        var empty = Assert.Single(results, r => r.BusinessUnit!.ExternalKey == "账套003");
        Assert.Equal("no_new_backup", empty.Status);
    }
}
