using BackupMonitor.Agent;
using BackupMonitor.Infrastructure.Recognition;
using BackupMonitor.Shared.Models.Admin;
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

    /// <summary>
    /// 拿推断出来的方案，在真实文件系统上跑一遍 Agent 的扫描。
    ///
    /// 「向导说会识别出什么，实际扫描就必须识别出什么」是这个模块的硬约束，
    /// 所以新结构的用例一律断言到这一步，而不是只断言推断出来的那段 JSON 好看。
    /// </summary>
    private static List<BackupScanResult> Scan(RecognizerProposalDto proposal)
    {
        var scanner = new BackupScanner(NullLogger<BackupScanner>.Instance);
        return scanner.ScanAsync(new AgentTaskConfigDto
        {
            TaskId = Guid.NewGuid(),
            Name = "按推断方案实扫",
            ApplicationName = proposal.SuggestedApplicationName ?? "test",
            SourcePath = proposal.SourcePath.Replace('/', Path.DirectorySeparatorChar),
            RecognizerType = proposal.RecognizerType,
            TaskMode = "automatic",
            Enabled = true,
            RecognizerConfig = proposal.RecognizerConfig,
            StabilityIntervalSeconds = 0
        }, CancellationToken.None).ToListAsync().GetAwaiter().GetResult();
    }

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

    /// <summary>
    /// 文件名每天不同（带日期）时，不能什么都不推荐。
    ///
    /// 这里原先断言的是退回到 *.bak / *.trn ——按扩展名统计是当时唯一的兜底手段。
    /// 现在中间多了一档「归一化模式」，同一批文件收敛成 db_*.bak / log_*.trn，
    /// 比扩展名更精确：*.bak 会把任何一个 .bak 都算作到场，db_*.bak 不会。
    /// 断言跟着改成新的期望，测试的本意（不能干脆什么都不推荐）没有变。
    /// </summary>
    [Fact]
    public void 文件名每天不同时按归一化模式认必需文件()
    {
        foreach (var day in new[] { "20260823", "20260824", "20260825" })
        {
            Write($@"{day}\db_{day}.bak");
            Write($@"{day}\log_{day}.trn");
        }

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);
        var recommended = proposal.RequiredFileCandidates.Where(c => c.Recommended).Select(c => c.Pattern).ToList();

        Assert.Contains("db_*.bak", recommended);
        Assert.Contains("log_*.trn", recommended);
        Assert.All(
            proposal.RequiredFileCandidates.Where(c => c.Recommended),
            c => Assert.Equal("pattern", c.Kind));
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
    // B5：中文量词形态靠「年」「月」这两个字自证，不需要放宽数字位数下限——
    // 上面 ZT001、账套001 这些反例照旧不成立。
    [InlineData("2026年", true, false)]
    [InlineData("08月", false, true)]
    [InlineData("25日", false, true)]
    [InlineData("第01车间", false, false)]
    [InlineData("2026年13月", false, false)]
    public void 年月目录识别(string name, bool isYear, bool isMonthOrDay)
    {
        Assert.Equal(isYear, StructureInference.LooksLikeYear(name));
        Assert.Equal(isMonthOrDay, StructureInference.LooksLikeMonthOrDay(name));
    }

    // ---------- B5：中文年月分层 ----------

    /// <summary>
    /// `备份\2026年\08月\*.bak` 此前落到「没能看懂」（置信度 25），因为 LooksLikeYear
    /// 要求整名全是 ASCII 数字。中文年月在国产 ERP 和手写批处理里很常见。
    /// </summary>
    [Fact]
    public void 中文年月分层能走情形零()
    {
        for (var day = 1; day <= 5; day++)
            Write($@"2026年\08月\db_2026-08-{day:00}.bak", new string('x', 4096));

        var proposal = StructureInference.Infer(Snapshot(maxDepth: 4), DateTime.UtcNow, 4);

        Assert.True(proposal.Confidence >= 50, $"置信度过低：{proposal.Confidence}");
        // 源路径必须带通配，否则到了 9 月任务就失效，且失效的表现是 path_not_found。
        Assert.EndsWith("/*/*", proposal.SourcePath, StringComparison.Ordinal);
    }

    // ---------- B3：尺寸基线建议 ----------

    /// <summary>
    /// 判据（备份大小低于下限判 size_abnormal）本来就存在且告警链路完整，
    /// 缺的只有建议值——要人凭空想一个字节数填进去，于是现场基本不会生效。
    /// </summary>
    [Fact]
    public void 单元够多且大小接近时给出下限建议()
    {
        // 6 个单元，每份 4 MB 上下（差异控制在离散度闸门以内）
        for (var unit = 1; unit <= 6; unit++)
            Write($@"ZT{unit:000}\db.bak", new string('x', 4 * 1024 * 1024 + unit * 1024));

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.NotNull(proposal.SuggestedMinTotalBytes);
        // 取最小一份的一半、向下取整到 MB。
        Assert.Equal(2L * 1024 * 1024, proposal.SuggestedMinTotalBytes);
        Assert.NotNull(proposal.SizeBaselineNote);
        Assert.Contains("建议下限", proposal.SizeBaselineNote!, StringComparison.Ordinal);
    }

    /// <summary>样本太少不给建议值——给了就是瞎猜，而这个数会产生告警。</summary>
    [Fact]
    public void 单元太少时不给建议值并说明原因()
    {
        for (var unit = 1; unit <= 2; unit++)
            Write($@"ZT{unit:000}\db.bak", new string('x', 4 * 1024 * 1024));

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Null(proposal.SuggestedMinTotalBytes);
        Assert.NotNull(proposal.SizeBaselineNote);
        Assert.Contains("样本太少", proposal.SizeBaselineNote!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 离散度闸门：MinTotalBytes 是**任务级**的单一阈值，而判定是按每个业务单元做的。
    /// 大小相差百倍时任何统一下限都必然在一头出错——宁可不给，也不要给一个假装有用的数。
    /// </summary>
    [Fact]
    public void 各单元大小相差过大时不给建议值()
    {
        Write(@"ZT001\db.bak", new string('x', 64 * 1024));
        Write(@"ZT002\db.bak", new string('x', 4 * 1024 * 1024));
        Write(@"ZT003\db.bak", new string('x', 8 * 1024 * 1024));

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Null(proposal.SuggestedMinTotalBytes);
        Assert.Contains("没有意义", proposal.SizeBaselineNote ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// 平铺目录里「每次丢一个文件进来，只认最新的那个」——一份备份就是一个文件。
    ///
    /// 这一支原先一份样本都交不出来（leaves 说的是目录单元，这里一个都没有），
    /// 于是同一屏上先说「里堆着 14 个备份文件」，紧接着说「只观察到 0 份备份，样本太少」。
    /// </summary>
    [Fact]
    public void 平铺目录取最新时按文件大小给出下限建议()
    {
        for (var day = 1; day <= 14; day++)
            Write($@"seeyon_backup_2026_09_{day:00}.bak", new string('x', 4 * 1024 * 1024 + day * 1024));

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("latest_single_file", proposal.RecognizerType);
        Assert.NotNull(proposal.SuggestedMinTotalBytes);
        Assert.Equal(2L * 1024 * 1024, proposal.SuggestedMinTotalBytes);
        Assert.DoesNotContain("样本太少", proposal.SizeBaselineNote ?? "", StringComparison.Ordinal);
        Assert.Contains("14 份备份", proposal.SizeBaselineNote!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 按组认的平铺目录（同一天的几个库凑一份备份）同样要按「一组多大」给样本，
    /// 而不是把整个目录算成一份。
    /// </summary>
    [Fact]
    public void 每天一组的平铺目录按组大小给出下限建议()
    {
        for (var day = 1; day <= 7; day++)
        {
            Write($@"beeServer_backup_2026_09_{day:00}_000004.bak", new string('x', 2 * 1024 * 1024));
            Write($@"dzwl_backup_2026_09_{day:00}_000004.bak", new string('x', 6 * 1024 * 1024 + day * 1024));
        }

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("multi_file_set", proposal.RecognizerType);
        // 每组 8 MB 上下，取最小一组的一半。
        Assert.Equal(4L * 1024 * 1024, proposal.SuggestedMinTotalBytes);
        Assert.Contains("7 份备份", proposal.SizeBaselineNote!, StringComparison.Ordinal);
    }

    // ---------- B4：周期与缺口不影响置信度 ----------

    /// <summary>
    /// 有缺口与没缺口的置信度必须**完全相同**。缺口是被识别对象的性质，
    /// 不是识别质量的问题——混在一起会让「这个目录有历史缺备」表现成「向导看不懂它」。
    /// </summary>
    [Fact]
    public void 历史缺口不影响置信度()
    {
        for (var day = 1; day <= 20; day++)
            Write($@"2026080{day:00}\db.bak".Replace("2026080", "202608"), new string('x', 4096));

        var full = StructureInference.Infer(Snapshot(), DateTime.UtcNow);
        Assert.Equal("每天", full.DetectedPeriod);
        Assert.Empty(full.MissingBackupDates);

        Directory.Delete(Path.Combine(_root, "20260805"), recursive: true);
        Directory.Delete(Path.Combine(_root, "20260812"), recursive: true);

        var gapped = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal(full.Confidence, gapped.Confidence);
        Assert.Equal(["2026-08-05", "2026-08-12"], gapped.MissingBackupDates);
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
    /// <summary>
    /// 元旦刚建出来、还空着的 2027 不能顶掉真正有备份的 2026。
    ///
    /// 只按名字挑"最新的年"的话，向导会对一个它本来看得懂的结构回一句"没能看懂"；
    /// 而扫描端的通配展开按内容时间挑，根本不会选中那个空目录——两边对"最新"的
    /// 判断在这里岔开，正是这类缺陷最难被发现的形态。
    /// </summary>
    [Fact]
    public void 空的新年份目录不会让推断失败()
    {
        BuildSeeyonLayout();
        Directory.CreateDirectory(Path.Combine(_root, "2027"));

        var proposal = StructureInference.Infer(Snapshot(maxDepth: 4), DateTime.UtcNow);

        Assert.Equal("multi_file_set", proposal.RecognizerType);
        Assert.EndsWith("/*/*", proposal.SourcePath, StringComparison.Ordinal);
        Assert.Contains(proposal.Evidence, e => e.Contains("最新的是 2026", StringComparison.Ordinal));
    }

    /// <summary>
    /// 推断产出通配源路径之后，服务端必须能把它展开回正确的目录再预演。
    ///
    /// 这两步正是 RecognizerWizardService.InferAsync 做的事（那条链路要连数据库，
    /// 这里直接测它调用的那两个函数）。预演跑在没展开的 Backup 上会得出"没有备份"——
    /// 规则是对的，只是预演找错了目录，而这是向导最不该撒的那种谎。
    /// </summary>
    [Fact]
    public void 通配方案能展开回正确目录并预演出最新一组()
    {
        BuildSeeyonLayout();

        var snapshot = Browse(maxDepth: 4);
        var root = SnapshotNode.FromSnapshot(snapshot);
        var proposal = StructureInference.Infer(root, snapshot.CapturedAt);
        var rules = RecognizerRules.Parse(proposal.RecognizerConfig);

        var (expanded, failure) = SnapshotRecognizer.ExpandSource(root, proposal.SourcePath, rules);
        Assert.Null(failure);
        Assert.NotNull(expanded);
        Assert.Equal("08", expanded!.Name);

        var preview = SnapshotRecognizer.Preview(
            expanded,
            proposal.RecognizerType,
            rules,
            stabilityIntervalSeconds: 0,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);

        var unit = Assert.Single(preview.Units);
        Assert.Equal("passed", unit.Status);
        // 30 组里只认最新的一组，一组两个文件。
        Assert.Equal(2, unit.TotalFiles);
        Assert.All(unit.Files, f => Assert.StartsWith("2026-08-30@02_00", f.RelativePath, StringComparison.Ordinal));
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
        var scanned = new List<BackupScanResult>();
        await foreach (var one in scanner.ScanAsync(new AgentTaskConfigDto
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
        }, CancellationToken.None))
        {
            scanned.Add(one);
        }

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

    /// <summary>
    /// 没配 requiredFiles 时不能把所有文件都标成「必需」。
    ///
    /// 这一行原先是 `Required.Count == 0 || …`，于是向导界面上一边写着
    /// 「这次没有看出固定出现的文件，暂不检查」，一边给下面 14 个文件全挂上「必需」角标。
    /// </summary>
    [Fact]
    public void 没有必需文件时不把所有文件标成必需()
    {
        var rules = RecognizerRules.Parse("{}");

        Assert.False(rules.IsRequiredFile("db.bak"));
        Assert.False(rules.HasMissingRequired(["db.bak"]));
    }

    // ---------- 混合层级的业务单元 ----------

    /// <summary>
    /// 真实目录：D:\自动备份\ 下 ZT001、ZT002 是账套（第 1 层），
    /// 而 ZT201-ZT216 只是个分组容器，真正的账套是它下面那 16 个（第 2 层）。
    ///
    /// businessUnitDepth 是全树统一深度，任何取值都覆盖不了它：取 1 会把 ZT201-ZT216
    /// 整个当成一个单元（于是 16 个账套里 15 个不在监控范围内，且 ZT216 的 7 天备份
    /// 被混成一份判 passed）；取 2 又会把 ZT001 的日期目录当成单元。
    /// </summary>
    private void BuildMixedDepthLayout()
    {
        foreach (var unit in new[] { "ZT001", "ZT002" })
        foreach (var day in new[] { "20260827", "20260828" })
        {
            Write($@"{unit}\{day}\UFDATA.BAK", new string('x', 2048));
            Write($@"{unit}\{day}\UfErpAct.Lst", "unit-list");
        }

        for (var i = 201; i <= 216; i++)
        foreach (var day in new[] { "20260827", "20260828" })
        {
            Write($@"ZT201-ZT216\ZT{i}\{day}\UFDATA.BAK", new string('x', 2048));
            Write($@"ZT201-ZT216\ZT{i}\{day}\UfErpAct.Lst", "unit-list");
        }
    }

    [Fact]
    public void 层级不齐时把分组容器下面那层认成业务单元()
    {
        BuildMixedDepthLayout();

        var proposal = StructureInference.Infer(Snapshot(maxDepth: 5), DateTime.UtcNow);

        Assert.Equal("subdirectory_units", proposal.RecognizerType);

        // 深度写不死，必须落到自适应那条规则上。
        var rules = RecognizerRules.Parse(proposal.RecognizerConfig);
        Assert.Equal("date_leaf", rules.UnitLayout);

        // 摘要里的数必须是真实的单元数：2 + 16 = 18，而不是第一层的 3 个。
        Assert.Contains("18 个业务单元", proposal.Summary, StringComparison.Ordinal);
        Assert.Contains(proposal.Evidence,
            e => e.Contains("ZT201-ZT216", StringComparison.Ordinal)
                 && e.Contains("不是业务单元", StringComparison.Ordinal));
    }

    /// <summary>
    /// 向导说 18 个，实际扫描就必须扫出 18 个——这条一致性是整个向导的立论点。
    /// 特别要盯住 ZT201–ZT215：按老规则它们一个都不会出现在结果里，而且毫无提示。
    /// </summary>
    [Fact]
    public void 层级不齐时预演与实际扫描都给出十八个单元()
    {
        BuildMixedDepthLayout();

        var snapshot = Browse(maxDepth: 5);
        var proposal = StructureInference.Infer(SnapshotNode.FromSnapshot(snapshot), snapshot.CapturedAt);
        var rules = RecognizerRules.Parse(proposal.RecognizerConfig);

        var preview = SnapshotRecognizer.Preview(
            SnapshotNode.FromSnapshot(snapshot), proposal.RecognizerType, rules,
            stabilityIntervalSeconds: 0, snapshot.CapturedAt, snapshot.DeniedPaths, snapshot.Truncated);

        Assert.Equal(18, preview.Units.Count);
        Assert.Equal(18, preview.PassedCount);
        Assert.Contains(preview.Units, u => u.BusinessUnit == "ZT001");
        Assert.Contains(preview.Units, u => u.BusinessUnit == "ZT201-ZT216/ZT201");
        Assert.Contains(preview.Units, u => u.BusinessUnit == "ZT201-ZT216/ZT215");

        // 每个单元只取最新的那个日期目录，不能把两天混成一份。
        Assert.All(preview.Units, u => Assert.Equal(2, u.TotalFiles));

        var scans = Scan(proposal);
        Assert.Equal(18, scans.Count);
        Assert.Equal(
            preview.Units.Select(u => u.BusinessUnit).OrderBy(x => x, StringComparer.Ordinal),
            scans.Select(s => s.BusinessUnit?.ExternalKey).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// 推断用的下探层数必须随配置一起交出去。
    ///
    /// 这个数原本只活在推断代码里：推断显式用 4，向导预演和 Agent 真扫都取
    /// BusinessUnitResolver.DefaultMaxDepth（6）。快照只有 4 层时预演无处可去、碰巧一致；
    /// Agent 面对的是真实文件系统，date_leaf 会一路下探到第 6 层——于是向导展示的单元
    /// 与实际扫描出的单元静默地不是同一批，external_key（取的就是相对路径）也跟着变。
    /// 所以这里盯住两件事：配置里带着 unitMaxDepth，且据它算出来的单元三方完全一致。
    /// </summary>
    [Fact]
    public void 单元下探层数写进配置让预演与真扫认同一批单元()
    {
        // 单元在第 2 层（分组容器下面），另加一个第 1 层的单元凑出「层级不齐」以走上 date_leaf；
        // 再挂一条比推断能看的还深的支路——正是它会让真扫比向导多下探一层。
        foreach (var day in new[] { "20260827", "20260828" })
            Write($@"ZT001\{day}\UFDATA.BAK", new string('x', 2048));

        foreach (var unit in new[] { "ZT101", "ZT102" })
        foreach (var day in new[] { "20260827", "20260828" })
            Write($@"group\{unit}\{day}\UFDATA.BAK", new string('x', 2048));

        Write(@"group\ZT103\sub1\sub2\leaf\UFDATA.BAK", new string('x', 2048));

        var snapshot = Browse(maxDepth: 6);
        var source = SnapshotNode.FromSnapshot(snapshot);
        var proposal = StructureInference.Infer(source, snapshot.CapturedAt);
        var rules = RecognizerRules.Parse(proposal.RecognizerConfig);

        Assert.Equal("subdirectory_units", proposal.RecognizerType);
        Assert.Equal("date_leaf", rules.UnitLayout);

        // 推断把自己用过的层数落进了配置，而且这个键是被承认的键名——
        // 不进 KnownKeys 的话，校验端会把它报成拼错的键。
        Assert.Contains("unitMaxDepth", proposal.RecognizerConfig, StringComparison.Ordinal);
        Assert.NotNull(rules.UnitMaxDepth);
        Assert.Empty(RecognizerRules.FindUnknownKeys(proposal.RecognizerConfig));

        // 拿这份配置重新解一遍单元：不传 maxDepth，走的正是预演与 Agent 那条
        // 「从规则里取层数」的路径，结果必须与推断报告的那批一模一样。
        var resolved = BusinessUnitResolver.Resolve(source, rules, SnapshotRecognizer.SnapshotNavigator(source));

        Assert.Equal(4, resolved.Count);
        Assert.Contains("4 个业务单元", proposal.Summary, StringComparison.Ordinal);

        // 深支路停在第 4 层。少了 unitMaxDepth 它会变成 group/ZT103/sub1/sub2/leaf——
        // 单元个数没变，键却换了一个，这正是那种谁都不会察觉的分叉。
        Assert.Contains(resolved, u => u.RelativePath == "group/ZT103/sub1/sub2");

        var scans = Scan(proposal);
        Assert.Equal(
            resolved.Select(u => (string?)u.RelativePath).OrderBy(x => x, StringComparer.Ordinal),
            scans.Select(s => s.BusinessUnit?.ExternalKey).OrderBy(x => x, StringComparer.Ordinal));
    }

    // ---------- 名字带年份的附件库 ----------

    /// <summary>
    /// U8 的 UFFile_{账套}_{年度}.dat 是按年度分的附件库，各账套的**启用年度不同**——
    /// 2022 年起用的有 5 个，2024 年起用的只有 3 个，两者都是完整备份。
    ///
    /// 这条模式一度「被看见但不被勾选」，理由是个数会浮动、不能当判据。前半句对，
    /// 后半句下过了头：requiredFiles 判的从来不是个数，是「至少匹配到一个」。
    /// 于是「附件库一个都不剩」没有任何一道检查拦得住——主库还在、文件数还够、
    /// 总大小还超（附件库比主库小得多）。所以现在它是判据，判的是整批丢失。
    /// </summary>
    [Fact]
    public void 个数随账套变化的附件库按至少有一个当判据()
    {
        var startYears = new[] { 2022, 2023, 2024 };
        for (var i = 0; i < startYears.Length; i++)
        {
            var unit = $"ZT{i + 1:000}";
            Write($@"{unit}\20260828\UFDATA.BAK", new string('x', 2048));
            Write($@"{unit}\20260828\UfErpAct.Lst", "unit-list");
            for (var year = startYears[i]; year <= 2026; year++)
                Write($@"{unit}\20260828\UFFile_{i + 1:000}_{year}.dat", new string('x', 512));
        }

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);
        var candidate = Assert.Single(
            proposal.RequiredFileCandidates.Where(c => c.Pattern.StartsWith("UFFile", StringComparison.Ordinal)));

        Assert.Equal("pattern", candidate.Kind);
        Assert.True(candidate.Recommended);
        Assert.Null(candidate.NotRecommendedReason);
        // 个数区间照常给出来：它是「判据不是个数」这句话的依据，人要看得见。
        Assert.Equal(3, candidate.CountPerUnitMin);
        Assert.Equal(5, candidate.CountPerUnitMax);
        Assert.Contains(proposal.Evidence, e => e.Contains("至少有一个", StringComparison.Ordinal));

        var rules = RecognizerRules.Parse(proposal.RecognizerConfig);
        Assert.Contains(rules.Required, p => p.StartsWith("UFFile", StringComparison.Ordinal));
        Assert.Contains("UFDATA.BAK", rules.Required);

        // 个数不同的账套都算完整：启用年度早的 5 个、晚的 3 个，一个都不该报警。
        var scans = Scan(proposal);
        Assert.All(scans, s => Assert.Equal("passed", s.Status));
        var files = scans.Single(s => s.BusinessUnit?.ExternalKey == "ZT001").Files;
        Assert.Equal(5, files.Count(f => f.RelativePath.StartsWith("UFFile", StringComparison.Ordinal)));

        // 而附件库整批丢了，就是不完整——这正是原先漏掉的那种故障。
        foreach (var dat in Directory.GetFiles(Path.Combine(_root, "ZT001", "20260828"), "UFFile_*.dat"))
            File.Delete(dat);

        var afterLoss = Scan(proposal).Single(s => s.BusinessUnit?.ExternalKey == "ZT001");
        Assert.Equal("required_file_missing", afterLoss.Status);
    }

    // ---------- 平铺目录里每天一组多个库 ----------

    /// <summary>
    /// 真实目录：D:\数据库备份\ 没有日期子目录，14 个 .bak 全平铺着，日期写在文件名里。
    /// 同一天的 beeServer + dzwl_product 才构成一次完整备份，14 个 = 7 天 × 2 个库。
    ///
    /// 按老规则这里会判成 latest_single_file，而那个识别器又会把整个目录收进来，
    /// 于是「一次备份」= 7 天的全部历史，每次上传 10.59GB 而不是 1.5GB。
    /// </summary>
    private void BuildFlatDatedDumps()
    {
        for (var day = 22; day <= 28; day++)
        {
            Write($"beeServer_backup_2026_08_{day}_000004_{2256969 + day}.bak", new string('x', 1024));
            Write($"dzwl_product_backup_2026_08_{day}_000004_{4679193 + day}.bak", new string('x', 8192));
        }
    }

    [Fact]
    public void 平铺目录里按文件名日期分组认出每天一组两个库()
    {
        BuildFlatDatedDumps();

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);

        Assert.Equal("multi_file_set", proposal.RecognizerType);

        var rules = RecognizerRules.Parse(proposal.RecognizerConfig);
        Assert.False(rules.Recursive);
        Assert.NotNull(rules.GroupBy);
        Assert.Contains("beeServer_backup_*.bak", rules.Required);
        Assert.Contains("dzwl_product_backup_*.bak", rules.Required);

        Assert.Contains("每天一组", proposal.Summary, StringComparison.Ordinal);
        Assert.Contains("2026_08_28", proposal.Summary, StringComparison.Ordinal);

        // 一次备份 = 最新那天的 2 个文件，不是全部 14 个。
        var scan = Assert.Single(Scan(proposal));
        Assert.Equal(2, scan.Files.Count);
        Assert.All(scan.Files, f => Assert.Contains("2026_08_28", f.RelativePath, StringComparison.Ordinal));
    }

    /// <summary>某天某个库没转储出来时，那一组就该判缺文件——这正是要告警的情况。</summary>
    [Fact]
    public void 最新一组少一个库时判为缺必需文件()
    {
        BuildFlatDatedDumps();

        // 规则要在目录还健康的时候推断出来——真实世界里也是这个顺序：
        // 先按正常的目录建任务，之后某一天某个库没转储出来。
        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);
        File.Delete(Path.Combine(_root, "beeServer_backup_2026_08_28_000004_2256997.bak"));

        var scan = Assert.Single(Scan(proposal));
        Assert.Equal("required_file_missing", scan.Status);
        Assert.Single(scan.Files);
    }

    /// <summary>
    /// 自适应猜错时的逃生门：人直接指出单元在哪，通配写相对源目录的路径。
    ///
    /// 自适应没有手动覆盖就是死局，所以这条路必须真的通。
    /// 同时盯住一个容易静默失效的点：businessUnitPaths 是解析期往列表里 AddRange 的，
    /// 而 Parse 内部用 `with` 复制了好几次记录——复制没保住这个列表的话，
    /// 表现是配置写了却完全不生效，且没有任何报错。
    /// </summary>
    [Fact]
    public void 人工指定的单元路径优先于自适应()
    {
        BuildMixedDepthLayout();

        var config = """{"businessUnitPaths":["ZT201-ZT216/ZT20*"],"requiredFiles":["UFDATA.BAK"]}""";
        var rules = RecognizerRules.Parse(config);

        // 解析结果要挺过 Parse 内部的多次 with 复制。
        Assert.Equal(["ZT201-ZT216/ZT20*"], rules.BusinessUnitPaths);
        Assert.Empty(RecognizerRules.FindUnknownKeys(config));

        var snapshot = Browse(maxDepth: 5);
        var preview = SnapshotRecognizer.Preview(
            SnapshotNode.FromSnapshot(snapshot), "subdirectory_units", rules,
            stabilityIntervalSeconds: 0, snapshot.CapturedAt, snapshot.DeniedPaths, snapshot.Truncated);

        // ZT201–ZT209 共 9 个，ZT001/ZT002 与 ZT210+ 都不在通配里。
        Assert.Equal(9, preview.Units.Count);
        Assert.All(preview.Units, u => Assert.StartsWith("ZT201-ZT216/ZT20", u.BusinessUnit!, StringComparison.Ordinal));
    }

    /// <summary>
    /// 一个今天没跑出备份的账套，必须照常出现在结果里并判「没有备份」，
    /// 而不是从单元列表里消失——「没备份」表现为「没这个账套」是最坏的一种静默。
    /// </summary>
    [Fact]
    public void 还没有备份的账套仍然是一个单元()
    {
        BuildMixedDepthLayout();
        Directory.CreateDirectory(Path.Combine(_root, "ZT201-ZT216", "ZT217"));

        var snapshot = Browse(maxDepth: 5);
        var rules = RecognizerRules.Parse("""{"unitLayout":"date_leaf","requiredFiles":["UFDATA.BAK"]}""");
        var preview = SnapshotRecognizer.Preview(
            SnapshotNode.FromSnapshot(snapshot), "subdirectory_units", rules,
            stabilityIntervalSeconds: 0, snapshot.CapturedAt, snapshot.DeniedPaths, snapshot.Truncated);

        var empty = Assert.Single(preview.Units.Where(u => u.BusinessUnit == "ZT201-ZT216/ZT217"));
        Assert.Equal("no_new_backup", empty.Status);
        Assert.Equal(19, preview.Units.Count);
    }

    /// <summary>
    /// latest_single_file 必须名副其实：只取最新的那一个文件。
    ///
    /// 它此前只用「最新」挑出那个文件所在的**目录**，随后照常收集整个目录——
    /// 于是「取最新的那个」实际取的是全部。在堆了 7 天备份的目录上，
    /// 后果是每次扫描把全部文件读一遍算 SHA-256、每次上传整个目录。
    /// </summary>
    [Fact]
    public void 最新单文件只收一个文件()
    {
        for (var day = 21; day <= 25; day++)
            Write($"db_202608{day}.bak", new string('x', 4096));

        var proposal = StructureInference.Infer(Snapshot(), DateTime.UtcNow);
        Assert.Equal("latest_single_file", proposal.RecognizerType);

        var scan = Assert.Single(Scan(proposal));
        var file = Assert.Single(scan.Files);
        Assert.Equal("db_20260825.bak", file.RelativePath);
    }
}
