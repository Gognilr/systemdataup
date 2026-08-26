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

    /// <summary>
    /// 致远 OA：一份备份 = 同名的 .zip（数据）+ .properties（元数据），一个月约 30 组。
    ///
    /// 两种扩展名数量完全相同，稳定排序下 .properties 会排在 .zip 前面（p < z），
    /// 于是向导曾经产出 {"includePatterns":["*.properties"]}——真正的数据包被规则
    /// 彻底排除，而元数据每天准时出现，任务天天判 passed。
    /// 所以这里断言的不是"推荐得好不好"，而是"*.zip 绝不能被漏掉"。
    /// </summary>
    [Fact]
    public void 同名成对文件时数据包必须进必需文件()
    {
        for (var day = 1; day <= 30; day++)
        {
            var stem = $"2026-08-{day:00}@02_00";
            Write($"{stem}.zip", new string('x', 4096));
            Write($"{stem}.properties", "meta");
        }

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        var required = RecognizerRules.Parse(proposal.RecognizerConfig).Required;
        Assert.Contains("*.zip", required);

        // includePatterns 可以不写；一旦写了，*.zip 必须在里面。
        var includes = RecognizerRules.Parse(proposal.RecognizerConfig).Includes;
        if (includes.Count > 0)
            Assert.Contains("*.zip", includes);

        Assert.Equal("multi_file_set", proposal.RecognizerType);

        // requiredFiles 保证 zip 不被排除，groupBy 保证 30 天不被算成一份。少任何一半都还是错的。
        Assert.Equal("basename", RecognizerRules.Parse(proposal.RecognizerConfig).GroupBy);

        // 依据要说清楚看到了什么，否则"确认"就是盲点头。
        Assert.Contains(proposal.Evidence, e => e.Contains("30 组", StringComparison.Ordinal));
        Assert.Contains(proposal.Evidence, e => e.Contains("*.zip", StringComparison.Ordinal)
                                                && e.Contains("*.properties", StringComparison.Ordinal));
    }

    /// <summary>
    /// 反向用例：没有成对文件时不能改坏已有行为，仍然走 latest_single_file。
    /// </summary>
    [Fact]
    public void 没有成对文件时仍然按最新单文件推断()
    {
        for (var day = 1; day <= 30; day++)
            Write($"db_2026-08-{day:00}.bak", new string('x', 4096));

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("latest_single_file", proposal.RecognizerType);
        Assert.Equal(["*.bak"], RecognizerRules.Parse(proposal.RecognizerConfig).Includes);
    }

    /// <summary>
    /// 平局用例：两种扩展名各 5 个、文件名互不成对，只能靠体积决胜。
    /// 不按体积决胜的话，胜负取决于文件枚举顺序——正是 R1 那个缺陷的成因。
    /// </summary>
    [Fact]
    public void 扩展名数量打平时按体积决胜()
    {
        for (var i = 1; i <= 5; i++)
        {
            Write($"payload{i}.zip", new string('x', 200_000));
            Write($"meta{i}.properties", "kv");
        }

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("latest_single_file", proposal.RecognizerType);
        Assert.Equal(["*.zip"], RecognizerRules.Parse(proposal.RecognizerConfig).Includes);
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

    // ---------- 年/月数字分层 ----------

    [Theory]
    [InlineData("2026", true, false)]
    [InlineData("1990", true, false)]
    [InlineData("2100", true, false)]
    [InlineData("1989", false, false)]
    [InlineData("08", false, true)]
    [InlineData("8", false, true)]
    [InlineData("12", false, true)]
    [InlineData("31", false, true)]
    [InlineData("32", false, false)]
    [InlineData("00", false, false)]
    [InlineData("ZT001", false, false)]
    [InlineData("账套001", false, false)]
    // 8 位数字既不是年也不是月——它归 LooksLikeDate 管，两条判定不能互相抢。
    [InlineData("20260817", false, false)]
    [InlineData("2026年", false, false)]
    [InlineData("08月", false, false)]
    public void 年月目录识别(string name, bool isYear, bool isMonthOrDay)
    {
        Assert.Equal(isYear, StructureInference.LooksLikeYear(name));
        Assert.Equal(isMonthOrDay, StructureInference.LooksLikeMonthOrDay(name));
    }

    /// <summary>致远 OA：Backup\2025\12、Backup\2026\07（空）、Backup\2026\08（30 组）。</summary>
    private void BuildSeeyonLayout()
    {
        Write(@"2025\12\2025-12-31@02_00.zip", new string('x', 4096));
        Write(@"2025\12\2025-12-31@02_00.properties", "meta");
        Directory.CreateDirectory(Path.Combine(_root, "2026", "07"));
        for (var day = 1; day <= 30; day++)
        {
            var stem = $@"2026\08\2026-08-{day:00}@02_00";
            Write($"{stem}.zip", new string('x', 4096));
            Write($"{stem}.properties", "meta");
        }
    }

    [Fact]
    public void 推断出年月分层并给出通配源路径()
    {
        BuildSeeyonLayout();

        var proposal = StructureInference.Infer(Snapshot(maxDepth: 4), DateTime.UtcNow);

        Assert.Equal("multi_file_set", proposal.RecognizerType);
        Assert.True(proposal.Confidence >= 70, $"置信度过低：{proposal.Confidence}");
        // 年/月/组三层都规整，给 85；任一层比例落在 60%-90% 之间才降到 70。
        Assert.Equal(85, proposal.Confidence);

        // 源路径必须带通配，否则到了 9 月任务就失效，且失效的表现是 path_not_found。
        Assert.EndsWith("/*/*", proposal.SourcePath, StringComparison.Ordinal);

        var rules = RecognizerRules.Parse(proposal.RecognizerConfig);
        Assert.Equal("basename", rules.GroupBy);
        Assert.Contains("*.zip", rules.Required);
        Assert.False(rules.Recursive);
    }

    /// <summary>
    /// 结论要有依据。只给结论不给依据，"确认"就退化成盲点头——
    /// 这是整个向导的立论点，不是可有可无的文案。
    /// </summary>
    [Fact]
    public void 年月分层的依据说清楚了看到什么()
    {
        BuildSeeyonLayout();

        var proposal = StructureInference.Infer(Snapshot(maxDepth: 4), DateTime.UtcNow);

        Assert.True(proposal.Evidence.Count >= 3, $"依据太少：{proposal.Evidence.Count} 条");
        Assert.Contains(proposal.Evidence, e => e.Contains("年份目录", StringComparison.Ordinal)
                                                && e.Contains("2026", StringComparison.Ordinal));
        Assert.Contains(proposal.Evidence, e => e.Contains("月份目录", StringComparison.Ordinal)
                                                && e.Contains("08", StringComparison.Ordinal));
        Assert.Contains(proposal.Evidence, e => e.Contains("30 组", StringComparison.Ordinal)
                                                && e.Contains("*.zip", StringComparison.Ordinal));
        Assert.Contains(proposal.Evidence, e => e.Contains("只认最新的一组", StringComparison.Ordinal));

        // 一句人话要说清"只看最新那组"和"跨月自动跟随"。
        Assert.Contains("最新", proposal.Summary, StringComparison.Ordinal);
        Assert.Contains("跨月", proposal.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 数字分层这条分支同样受 HasGaps 约束：快照没抓全时置信度下调并说明，
    /// 而不是拿着看得见的那部分装作看懂了整棵树。
    ///
    /// 用月目录下一棵更深的子树来制造触顶——年/月两层本身在 maxDepth=3 就已经抓全了
    /// （Backup→年(1)→月(2)→文件(3)），把向导的推断深度提到 4 是给"再深一层"的结构
    /// 留余量，对本结构不改变结论。
    /// </summary>
    [Fact]
    public void 快照没抓全时年月分层的结论要声明不完整()
    {
        BuildSeeyonLayout();
        Write(@"2026\08\archive\2025\old.zip", "x");

        var deep = StructureInference.Infer(Snapshot(maxDepth: 6), DateTime.UtcNow);
        var shallow = StructureInference.Infer(Snapshot(maxDepth: 3), DateTime.UtcNow);

        // 两边都仍然认出了年/月分层——差别只在"这次抓全了没有"。
        Assert.Equal("multi_file_set", deep.RecognizerType);
        Assert.Equal("multi_file_set", shallow.RecognizerType);
        Assert.True(shallow.Confidence < deep.Confidence,
            $"抓不全却给了同样的置信度：{shallow.Confidence} vs {deep.Confidence}");
        Assert.Contains(shallow.Evidence, e => e.Contains("没有被完整抓取", StringComparison.Ordinal));
    }
    /// <summary>空的 07 不能顶掉有备份的 08。</summary>
    [Fact]
    public void 空月份目录不会被选成最新()
    {
        Write(@"2026\07\keep-me-out.zip", "x");
        File.Delete(Path.Combine(_root, "2026", "07", "keep-me-out.zip"));
        for (var day = 1; day <= 5; day++)
        {
            Write($@"2026\08\2026-08-{day:00}@02_00.zip", new string('x', 4096));
            Write($@"2026\08\2026-08-{day:00}@02_00.properties", "meta");
        }
        Directory.CreateDirectory(Path.Combine(_root, "2026", "09"));

        var proposal = StructureInference.Infer(Snapshot(maxDepth: 4), DateTime.UtcNow);

        Assert.Contains(proposal.Evidence, e => e.Contains("最新的是 08", StringComparison.Ordinal));
    }

    /// <summary>
    /// 数字分层这条新分支不能抢走既有的判定。ZT001\20260825\ 仍然是 subdirectory_units。
    /// </summary>
    [Fact]
    public void 数字分层不抢走账套加日期的结构()
    {
        BuildU8Layout(units: 3);

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("subdirectory_units", proposal.RecognizerType);
    }
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
    // groupBy：分组语义在 RecognizerRules 里实现一次、两侧共用，这几条是它不分叉的唯一自动化防线。
    [InlineData("multi_file_set", """{"recursive":true,"groupBy":"basename"}""")]
    [InlineData("subdirectory_units", """{"businessUnitDepth":1,"unitLayout":"latest_directory","groupBy":"basename"}""")]
    [InlineData("subdirectory_units", """{"businessUnitDepth":2,"groupBy":"^([A-Za-z]+)"}""")]
    [InlineData("latest_directory", """{"groupBy":"basename","requiredFiles":["UFDATA.BAK"]}""")]
    // 非法正则：两侧必须同样退化为不分组，而不是一侧抛异常、一侧照常。
    [InlineData("multi_file_set", """{"recursive":true,"groupBy":"^([unclosed"}""")]
    // 通配源路径：两侧各自展开，必须落到同一个目录再谈判定一致。
    [InlineData("latest_directory", """{"requiredFiles":["UFDATA.BAK"]}""", @"*")]
    [InlineData("multi_file_set", """{"recursive":true,"requiredFiles":["UFDATA.BAK"]}""", @"*\*")]
    public async Task 预演结果与实际扫描一致(string recognizerType, string recognizerConfig, string? sourcePattern = null)
    {
        BuildU8Layout(units: 3, incompleteUnits: "ZT002");
        var sourcePath = sourcePattern is null ? _root : Path.Combine(_root, sourcePattern);

        var scanner = new BackupScanner(NullLogger<BackupScanner>.Instance);
        var scanned = await scanner.ScanAsync(new AgentTaskConfigDto
        {
            TaskId = Guid.NewGuid(),
            Name = "一致性测试",
            ApplicationName = "用友 U8",
            SourcePath = sourcePath,
            RecognizerType = recognizerType,
            TaskMode = "automatic",
            Enabled = true,
            RecognizerConfig = recognizerConfig,
            StabilityIntervalSeconds = 0
        }, CancellationToken.None);

        var snapshot = Browse(maxDepth: 6, maxEntries: 20000);
        var rules = RecognizerRules.Parse(recognizerConfig);
        var snapshotRoot = SnapshotNode.FromSnapshot(snapshot);

        // 通配展开也要两侧一致：服务端在快照上展开，Agent 在真实磁盘上展开。
        // 先断言两边选中的是同一个目录，再比后面的判定，否则"文件列表一致"
        // 有可能是在两个不同目录上各自自洽。
        var (expandedSource, expandFailure) = SnapshotRecognizer.ExpandSource(snapshotRoot, sourcePath, rules);
        Assert.Null(expandFailure);
        Assert.NotNull(expandedSource);
        var previewBase = expandedSource!.FullPath;
        if (sourcePattern is not null)
        {
            Assert.Equal(
                Normalize(Path.GetRelativePath(_root, ExpandOnDisk(sourcePath))),
                Normalize(Path.GetRelativePath(_root, previewBase.Replace('/', Path.DirectorySeparatorChar))));
        }

        var preview = SnapshotRecognizer.Preview(
            expandedSource,
            recognizerType,
            rules,
            stabilityIntervalSeconds: 0,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);

        Assert.Equal(scanned.Count, preview.Units.Count);

        var scanBase = sourcePattern is null ? _root : ExpandOnDisk(sourcePath);
        var scannedByRoot = scanned
            .ToDictionary(s => Normalize(Path.GetRelativePath(scanBase, s.SourceRoot)), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>在真实磁盘上展开通配，供测试自己算出期望的采集根。</summary>
    private static string ExpandOnDisk(string pattern)
    {
        var expansion = WildcardPath.Expand(
            pattern,
            current => Directory.Exists(current)
                ? Directory.EnumerateDirectories(current).Select(p => new DirectoryInfo(p))
                : [],
            d => d.Name,
            d => d.FullName,
            d => d.EnumerateFiles("*", SearchOption.AllDirectories)
                  .Select(f => f.LastWriteTimeUtc)
                  .DefaultIfEmpty(DateTime.MinValue)
                  .Max());
        Assert.True(expansion.Success, expansion.FailureMessage);
        return expansion.Path!;
    }

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
         "unitLayout":"latest_directory","batchRegex":"^\\d{8}$","groupBy":"basename"}
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
