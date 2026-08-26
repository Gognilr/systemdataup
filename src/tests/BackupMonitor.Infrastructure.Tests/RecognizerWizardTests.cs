using BackupMonitor.Agent;
using BackupMonitor.Infrastructure.Recognition;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Recognition;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 建任务向导的三段：目录快照采集（Agent）→ 结构推断（服务端）→ 规则预演（服务端）。
///
/// 其中最关键的一条不是任何单点行为，而是「向导说会识别出什么，实际扫描就必须识别出什么」
/// ——见文末的一致性测试。向导一旦在这件事上撒谎，管理员会带着错误的信心离开，
/// 而错误要等到某天真正需要恢复数据时才暴露。
/// </summary>
public sealed class RecognizerWizardTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bm-wiz-" + Guid.NewGuid().ToString("N"));

    public RecognizerWizardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ---------- 造目录 ----------

    private void Write(string relativePath, string content = "backup-payload")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>用友 U8 的典型摆放：F:\autobak\账套\日期\{UFDATA.BAK, UfErpAct.Lst}</summary>
    private void BuildU8Layout(int units = 3, params string[] incompleteUnits)
    {
        for (var i = 1; i <= units; i++)
        {
            var unit = $"ZT{i:000}";
            foreach (var day in new[] { "20260823", "20260824", "20260825" })
            {
                Write($@"{unit}\{day}\UFDATA.BAK", new string('x', 2048));
                if (!incompleteUnits.Contains(unit) || day != "20260825")
                    Write($@"{unit}\{day}\UfErpAct.Lst", "unit-list");
            }
        }
    }

    private BrowseSnapshotDto Browse(string? path = null, int maxDepth = 3, int maxEntries = 3000)
    {
        var browser = new DirectoryBrowser(
            Options.Create(new AgentOptions()),
            NullLogger<DirectoryBrowser>.Instance);
        return browser.Browse(
            new BrowsePathCommandPayload { Path = path ?? _root, MaxDepth = maxDepth, MaxEntries = maxEntries },
            CancellationToken.None);
    }

    private SnapshotNode Snapshot(int maxDepth = 3) => SnapshotNode.FromSnapshot(Browse(maxDepth: maxDepth));

    // ---------- 目录快照 ----------

    [Fact]
    public void 快照只返回元数据且层级结构完整()
    {
        BuildU8Layout(units: 2);

        var snapshot = Browse();

        Assert.False(snapshot.IsDriveList);
        Assert.Equal(2, snapshot.Entries.Count(e => e.IsDirectory));

        var unit = snapshot.Entries.First(e => e.Name == "ZT001");
        var day = unit.Children.First(e => e.Name == "20260825");
        var file = day.Children.First(e => e.Name == "UFDATA.BAK");

        Assert.True(file.SizeBytes > 0);
        Assert.NotNull(file.LastModifiedAt);
        // 相对路径始终相对快照根，且用 '/' 分隔——服务端据此还原绝对路径。
        Assert.Equal("ZT001/20260825/UFDATA.BAK", file.RelativePath);
    }

    [Fact]
    public void 抓取深度到顶的目录会被标记而不是伪装成空目录()
    {
        BuildU8Layout(units: 1);

        var snapshot = Browse(maxDepth: 2);
        var unit = snapshot.Entries.First(e => e.Name == "ZT001");
        var day = unit.Children.First(e => e.Name == "20260825");

        Assert.Empty(day.Children);
        // "这里没抓" 和 "这里是空的" 必须能区分开，否则推断会据此得出错误结论。
        Assert.True(day.DepthLimited);
        Assert.True(day.ChildCount > 0);
    }

    [Fact]
    public void 条目数触顶时整次快照标记截断()
    {
        BuildU8Layout(units: 3);

        var snapshot = Browse(maxEntries: 10);

        Assert.True(snapshot.Truncated);
        Assert.True(snapshot.TotalEntries <= 10);
    }

    [Fact]
    public void 白名单之外的路径拒绝浏览()
    {
        BuildU8Layout(units: 1);
        var browser = new DirectoryBrowser(
            Options.Create(new AgentOptions { BrowseRoots = [Path.Combine(_root, "ZT001")] }),
            NullLogger<DirectoryBrowser>.Instance);

        // 白名单内放行
        var allowed = browser.Browse(
            new BrowsePathCommandPayload { Path = Path.Combine(_root, "ZT001", "20260825") },
            CancellationToken.None);
        Assert.NotEmpty(allowed.Entries);

        // 白名单外拒绝，且不是"返回空列表"这种看起来像成功的失败
        Assert.Throws<UnauthorizedAccessException>(() =>
            browser.Browse(new BrowsePathCommandPayload { Path = _root }, CancellationToken.None));
    }

    [Fact]
    public void 空路径返回磁盘列表作为浏览起点()
    {
        var snapshot = Browse(path: string.Empty);

        Assert.True(snapshot.IsDriveList);
        Assert.All(snapshot.Entries, e => Assert.True(e.IsDirectory));
    }

    // ---------- 结构推断 ----------

    [Fact]
    public void 推断出账套加日期的两层结构()
    {
        BuildU8Layout(units: 3);

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("subdirectory_units", proposal.RecognizerType);
        Assert.True(proposal.Confidence >= 70, $"置信度过低：{proposal.Confidence}");

        var rules = RecognizerRules.Parse(proposal.RecognizerConfig);
        Assert.Equal(1, rules.BusinessUnitDepth);
        Assert.Equal("latest_directory", rules.UnitLayout);

        // 两个每天都出现的文件应当被推荐为必需文件。
        var recommended = proposal.RequiredFileCandidates.Where(c => c.Recommended).Select(c => c.Pattern).ToList();
        Assert.Contains("UFDATA.BAK", recommended);
        Assert.Contains("UfErpAct.Lst", recommended);

        // 结论要有依据，也要有一句人话——两者缺一，"确认"就变成了盲点头。
        Assert.NotEmpty(proposal.Evidence);
        Assert.Contains("业务单元", proposal.Summary, StringComparison.Ordinal);
        Assert.Contains("20260825", proposal.Summary, StringComparison.Ordinal);
        Assert.Contains("UFDATA.BAK", proposal.Summary, StringComparison.Ordinal);
        Assert.Equal("用友 U8", proposal.SuggestedApplicationName);
    }

    [Fact]
    public void 推断出源目录下直接是日期目录的结构()
    {
        foreach (var day in new[] { "20260823", "20260824", "20260825" })
        {
            Write($@"{day}\full.bak");
            Write($@"{day}\full.trn");
        }

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("latest_directory", proposal.RecognizerType);
        Assert.True(proposal.Confidence >= 70);
        var recommended = proposal.RequiredFileCandidates.Where(c => c.Recommended).Select(c => c.Pattern).ToList();
        Assert.Contains("full.bak", recommended);
        Assert.Contains("full.trn", recommended);
    }

    [Fact]
    public void 同一目录里堆着历次备份时推断为最新单文件()
    {
        Write("db_20260823.bak");
        Write("db_20260824.bak");
        Write("db_20260825.bak");

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("latest_single_file", proposal.RecognizerType);
    }

    [Fact]
    public void 文件名每天不同时退回按扩展名认必需文件()
    {
        foreach (var day in new[] { "20260823", "20260824", "20260825" })
        {
            Write($@"{day}\db_{day}.bak");
            Write($@"{day}\log_{day}.trn");
        }

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);
        var recommended = proposal.RequiredFileCandidates.Where(c => c.Recommended).Select(c => c.Pattern).ToList();

        // 精确文件名一个都不重复，必须退一步按扩展名统计，而不是干脆什么都不推荐。
        Assert.Contains("*.bak", recommended);
        Assert.Contains("*.trn", recommended);
    }

    [Fact]
    public void 看不懂的结构给出低置信度而不是硬猜()
    {
        Write(@"文档\readme.txt");
        Write(@"图片\a.png");
        Write(@"随手记\b.docx");
        Directory.CreateDirectory(Path.Combine(_root, "空目录"));

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        // 三个子目录各有一个文件，会被当成"每个子目录一份备份"——这是合理猜测，
        // 但置信度不该高到可以免于人工确认。真正要保证的是：不会声称高置信度。
        Assert.True(proposal.Confidence <= 75, $"对杂乱结构给出了过高的置信度：{proposal.Confidence}");
    }

    [Fact]
    public void 快照不完整时置信度下调并说明()
    {
        BuildU8Layout(units: 3);

        var full = StructureInference.Infer(Snapshot(maxDepth: 3), DateTime.UtcNow);
        var partial = StructureInference.Infer(Snapshot(maxDepth: 2), DateTime.UtcNow);

        Assert.True(partial.Confidence < full.Confidence);
        Assert.Contains(partial.Evidence, e => e.Contains("没有被完整抓取", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("20260825", true)]
    [InlineData("2026-08-25", true)]
    [InlineData("2026_08_25", true)]
    [InlineData("20260825_0136", true)]
    [InlineData("260825", true)]
    [InlineData("ZT001", false)]
    [InlineData("账套001", false)]
    [InlineData("finance", false)]
    [InlineData("20261325", false)]
    public void 日期目录识别(string name, bool expected) =>
        Assert.Equal(expected, StructureInference.LooksLikeDate(name));

    // ---------- 规则预演 ----------

    [Fact]
    public void 预演逐个业务单元判定缺失必需文件()
    {
        // ZT002 的最新一天缺 UfErpAct.Lst
        BuildU8Layout(units: 3, incompleteUnits: "ZT002");

        var snapshot = Browse();
        var preview = SnapshotRecognizer.Preview(
            SnapshotNode.FromSnapshot(snapshot),
            "subdirectory_units",
            RecognizerRules.Parse("""{"businessUnitDepth":1,"unitLayout":"latest_directory","requiredFiles":["UFDATA.BAK","UfErpAct.Lst"]}"""),
            stabilityIntervalSeconds: 0,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);

        Assert.Equal(3, preview.Units.Count);
        Assert.Equal(2, preview.PassedCount);

        var broken = preview.Units.Single(u => u.Status != "passed");
        Assert.Contains("ZT002", broken.BusinessUnit);
        Assert.Equal("required_file_missing", broken.Status);
        Assert.Equal(["UfErpAct.Lst"], broken.MissingRequired);

        // 一个单元的问题不影响其它单元的判定。
        Assert.All(preview.Units.Where(u => u.BusinessUnit != broken.BusinessUnit),
            u => Assert.Equal("passed", u.Status));
    }

    [Fact]
    public void 预演按快照时刻回放稳定观察窗口()
    {
        BuildU8Layout(units: 1);
        var snapshot = Browse();

        var preview = SnapshotRecognizer.Preview(
            SnapshotNode.FromSnapshot(snapshot),
            "subdirectory_units",
            RecognizerRules.Parse("""{"businessUnitDepth":1,"unitLayout":"latest_directory"}"""),
            stabilityIntervalSeconds: 3600,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);

        // 文件刚写完，按快照时刻回放必然落在观察窗口内。
        Assert.Equal("still_changing", Assert.Single(preview.Units).Status);
    }

    /// <summary>
    /// 一致性：同一份规则、同一棵目录，向导的预演结果必须和 Agent 真扫的结果一致。
    ///
    /// 这是整个向导能不能被信任的支点。预演跑在快照上、扫描跑在真实文件系统上，
    /// 是两套遍历代码；一旦分叉，向导会用一个漂亮的界面给出错误的保证。
    /// </summary>
    [Theory]
    [InlineData("subdirectory_units", """{"businessUnitDepth":1,"unitLayout":"latest_directory","requiredFiles":["UFDATA.BAK","UfErpAct.Lst"]}""")]
    [InlineData("subdirectory_units", """{"businessUnitDepth":1,"unitLayout":"latest_directory"}""")]
    [InlineData("latest_directory", """{"requiredFiles":["UFDATA.BAK"]}""")]
    [InlineData("latest_single_file", """{"includePatterns":["*.bak"]}""")]
    [InlineData("multi_file_set", """{"requiredFiles":["*.bak"]}""")]
    [InlineData("multi_file_set", """{"recursive":false}""")]
    [InlineData("subdirectory_units", """{"businessUnitDepth":2,"requiredFiles":["UFDATA.BAK"]}""")]
    public async Task 预演结果与实际扫描一致(string recognizerType, string recognizerConfig)
    {
        BuildU8Layout(units: 3, incompleteUnits: "ZT002");

        var scanner = new BackupScanner(NullLogger<BackupScanner>.Instance);
        var scanned = await scanner.ScanAsync(new AgentTaskConfigDto
        {
            TaskId = Guid.NewGuid(),
            Name = "一致性测试",
            ApplicationName = "用友 U8",
            SourcePath = _root,
            RecognizerType = recognizerType,
            TaskMode = "automatic",
            Enabled = true,
            RecognizerConfig = recognizerConfig,
            StabilityIntervalSeconds = 0
        }, CancellationToken.None);

        var snapshot = Browse(maxDepth: 6, maxEntries: 20000);
        var preview = SnapshotRecognizer.Preview(
            SnapshotNode.FromSnapshot(snapshot),
            recognizerType,
            RecognizerRules.Parse(recognizerConfig),
            stabilityIntervalSeconds: 0,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);

        Assert.Equal(scanned.Count, preview.Units.Count);

        var scannedByRoot = scanned
            .ToDictionary(s => Normalize(Path.GetRelativePath(_root, s.SourceRoot)), StringComparer.OrdinalIgnoreCase);
        foreach (var unit in preview.Units)
        {
            var key = Normalize(unit.SourceRoot);
            Assert.True(scannedByRoot.ContainsKey(key), $"预演多出了一个扫描没有的采集根：{key}");
            var actual = scannedByRoot[key];

            Assert.Equal(actual.Status, unit.Status);
            Assert.Equal(actual.Files.Count, unit.TotalFiles);
            Assert.Equal(
                actual.Files.Select(f => f.RelativePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                unit.Files.Select(f => f.RelativePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            Assert.Equal(
                actual.Files.Where(f => f.IsRequired).Select(f => f.RelativePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                unit.Files.Where(f => f.IsRequired).Select(f => f.RelativePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// 指令参数在服务端用默认选项序列化（PascalCase），Agent 用小驼峰策略反序列化。
    /// 两边靠 PropertyNameCaseInsensitive 对上——这是个容易在某次"顺手统一一下序列化选项"
    /// 时被打破的隐式约定，而打破的表现是浏览指令永远按默认值执行，没有任何报错。
    /// </summary>
    [Fact]
    public void 浏览指令参数跨序列化约定仍能还原()
    {
        var serverSide = System.Text.Json.JsonSerializer.Serialize(new BrowsePathCommandPayload
        {
            Path = @"F:\autobak",
            MaxDepth = 4,
            MaxEntries = 1234
        });

        var agentSide = System.Text.Json.JsonSerializer.Deserialize<BrowsePathCommandPayload>(
            serverSide,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });

        Assert.NotNull(agentSide);
        Assert.Equal(@"F:\autobak", agentSide!.Path);
        Assert.Equal(4, agentSide.MaxDepth);
        Assert.Equal(1234, agentSide.MaxEntries);
    }

    /// <summary>
    /// 规则要求的层级比快照抓到的深时，预演不能说"这里没有备份"。
    ///
    /// 这是向导唯一可能撒谎的方向：文件明明在，只是这次没抓到那一层。
    /// 必须标记 Incomplete 并在文案里说明，否则管理员会据此去改一条本来正确的规则。
    /// </summary>
    [Fact]
    public void 规则超出快照深度时预演声明自己不完整()
    {
        BuildU8Layout(units: 2);

        // 快照只抓 2 层：账套（1）+ 日期目录（2），日期目录里的文件没抓到。
        var snapshot = Browse(maxDepth: 2);
        var preview = SnapshotRecognizer.Preview(
            SnapshotNode.FromSnapshot(snapshot),
            "subdirectory_units",
            RecognizerRules.Parse("""{"businessUnitDepth":2,"requiredFiles":["UFDATA.BAK"]}"""),
            stabilityIntervalSeconds: 0,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);

        Assert.NotEmpty(preview.Units);
        Assert.All(preview.Units, unit =>
        {
            Assert.Equal("no_new_backup", unit.Status);
            Assert.True(unit.Incomplete, "抓取深度不足的单元必须标记为不完整");
            Assert.Contains("未抓取完整", unit.FailureMessage);
        });
        Assert.True(preview.SnapshotDepthLimited);
    }

    // ---------- 识别规则键名校验 ----------

    [Fact]
    public void 拼错的键名会被找出来并给出建议()
    {
        var unknown = RecognizerRules.FindUnknownKeys("""{"requireFiles":["a.bak"],"recursive":true}""");

        Assert.Equal(["requireFiles"], unknown);
        Assert.Equal("requiredFiles", RecognizerRules.SuggestKey("requireFiles"));
        Assert.Equal("requiredFiles", RecognizerRules.SuggestKey("required_files"));
        Assert.Equal("businessUnitDepth", RecognizerRules.SuggestKey("businessUnitDept"));
    }

    [Fact]
    public void 全部合法键名不报未知()
    {
        var json = """
        {"includePatterns":["*.bak"],"excludePatterns":["*.log"],"excludeDirectories":["temp"],
         "requiredFiles":["a.bak"],"recursive":false,"businessUnitDepth":2,
         "unitLayout":"latest_directory","batchRegex":"^\\d{8}$"}
        """;

        Assert.Empty(RecognizerRules.FindUnknownKeys(json));
    }

    [Fact]
    public void 毫不相干的键名不硬给建议()
    {
        // 编辑距离太远时宁可不提示，也不要把人往错的方向指。
        Assert.Null(RecognizerRules.SuggestKey("完全不相干的东西"));
    }

    [Fact]
    public void 必需文件匹配大小写不敏感且可只写文件名()
    {
        var rules = RecognizerRules.Parse("""{"requiredFiles":["ufdata.bak"]}""");

        Assert.False(rules.HasMissingRequired(["20260825/UFDATA.BAK"]));
        Assert.True(rules.HasMissingRequired(["20260825/other.bak"]));
        Assert.True(rules.IsRequiredFile("sub/UFDATA.BAK"));
    }
}
