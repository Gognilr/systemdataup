using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using BackupMonitor.Shared.Models.Admin;

namespace BackupMonitor.Infrastructure.Recognition;

/// <summary>
/// 从目录快照反推识别规则。
///
/// 这是整个向导的立论点：运维对自己的备份目录是专家，对我们的抽象（识别器类型、
/// 业务单元、层级深度）是外行。所以不要求他描述结构——让他指一个目录，由系统看结构、
/// 出方案，再用一句人话说回去让他判断对错。"对/不对"是个人都能做的判断。
///
/// 因此 Evidence 和 Summary 一样重要：只给结论而不给依据，"确认"就退化成盲点头。
/// 同理，看不懂就必须老实说看不懂（Confidence 低），绝不能猜错了还装得很确定。
/// </summary>
public static class StructureInference
{
    /// <summary>某个文件要被推荐为"必需"，需要在这个比例的备份目录里都出现。</summary>
    private const double RequiredThreshold = 0.9;

    /// <summary>某一层要被判定为"日期层"，需要这个比例的目录名看起来像日期。</summary>
    private const double DateLayerThreshold = 0.6;

    /// <summary>
    /// 输出识别规则用的序列化选项。
    /// TypeInfoResolver 必须显式给：JsonNode.ToJsonString 传入自定义 options 时不会
    /// 回退到默认解析器，缺了它会在写 JsonArray 里的字符串时直接抛 InvalidOperationException。
    /// </summary>
    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static RecognizerProposalDto Infer(SnapshotNode source, DateTime capturedAt)
    {
        var directories = VisibleDirectories(source).ToList();
        var files = VisibleFiles(source).ToList();
        var evidence = new List<string>();

        if (directories.Count == 0)
            return InferFlatDirectory(source, files, evidence);

        var dateDirectories = directories.Where(d => LooksLikeDate(d.Name)).ToList();
        var dateRatio = (double)dateDirectories.Count / directories.Count;

        // 情形一：这一层本身就是日期层 —— 源目录下直接是 20260824、20260825……
        if (dateRatio >= DateLayerThreshold)
        {
            evidence.Add($"{source.Name} 下有 {directories.Count} 个子目录，其中 {dateDirectories.Count} 个的名字是日期");
            var latest = dateDirectories.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase).First();
            evidence.Add($"最新的一个是 {latest.Name}");

            var required = InferRequiredFiles(dateDirectories, evidence);
            var config = new JsonObject();
            AddRequired(config, required);

            return Build(
                source, "latest_directory", config,
                confidence: dateRatio >= 0.9 ? 90 : 72,
                summary: $"{source.Name} 下按日期建目录（共 {directories.Count} 个），最新的是 {latest.Name}"
                         + DescribeRequired(required),
                evidence, required, dateDirectories);
        }

        // 情形二：这一层是业务单元层，单元下面还有日期层 —— 账套001\20260825\…
        var unitsWithDateChildren = directories
            .Where(d => HasDateChildren(d))
            .ToList();
        var unitDateRatio = (double)unitsWithDateChildren.Count / directories.Count;

        if (unitDateRatio >= DateLayerThreshold)
        {
            var leaves = unitsWithDateChildren
                .Select(d => VisibleDirectories(d)
                    .Where(c => LooksLikeDate(c.Name))
                    .OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .First())
                .ToList();

            evidence.Add($"{source.Name} 下有 {directories.Count} 个子目录（{PreviewNames(directories)}）");
            evidence.Add($"其中 {unitsWithDateChildren.Count} 个的下一层是日期目录，结构彼此一致");
            var newestLeaf = leaves.OrderByDescending(l => l.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (newestLeaf is not null)
                evidence.Add($"最新的日期目录是 {newestLeaf.Name}");

            var required = InferRequiredFiles(leaves, evidence);
            var config = new JsonObject
            {
                ["businessUnitDepth"] = 1,
                ["unitLayout"] = "latest_directory"
            };
            AddRequired(config, required);

            return Build(
                source, "subdirectory_units", config,
                confidence: unitDateRatio >= 0.9 ? 92 : 74,
                summary: $"{source.Name} 下有 {directories.Count} 个业务单元（{PreviewNames(directories)}），"
                         + $"每个单元按日期建目录"
                         + (newestLeaf is not null ? $"，最新的是 {newestLeaf.Name}" : "")
                         + DescribeRequired(required),
                evidence, required, leaves);
        }

        // 情形三：这一层是业务单元层，单元里直接放备份文件 —— 账套001\*.bak
        var unitsWithFiles = directories.Where(d => VisibleFiles(d).Any()).ToList();
        var unitFileRatio = (double)unitsWithFiles.Count / directories.Count;

        if (unitFileRatio >= DateLayerThreshold)
        {
            evidence.Add($"{source.Name} 下有 {directories.Count} 个子目录（{PreviewNames(directories)}）");
            evidence.Add($"其中 {unitsWithFiles.Count} 个里面直接放着文件，没有再分日期目录");

            var required = InferRequiredFiles(unitsWithFiles, evidence);
            var config = new JsonObject { ["businessUnitDepth"] = 1 };
            AddRequired(config, required);

            return Build(
                source, "subdirectory_units", config,
                confidence: 70,
                summary: $"{source.Name} 下有 {directories.Count} 个子目录（{PreviewNames(directories)}），"
                         + "每个子目录自成一份备份" + DescribeRequired(required),
                evidence, required, unitsWithFiles);
        }

        // 看不懂。老实说看不懂，把观察到的现象原样交出去，由人来选模板。
        evidence.Add($"{source.Name} 下有 {directories.Count} 个子目录、{files.Count} 个文件");
        evidence.Add("子目录的命名看不出规律，也没有一致的层级结构");
        var fallbackConfig = new JsonObject();
        return Build(
            source, files.Count > 0 ? "multi_file_set" : "latest_directory", fallbackConfig,
            confidence: 25,
            summary: $"没能看懂 {source.Name} 的结构",
            evidence, [], []);
    }

    /// <summary>源目录下只有文件、没有子目录。</summary>
    private static RecognizerProposalDto InferFlatDirectory(
        SnapshotNode source, List<SnapshotNode> files, List<string> evidence)
    {
        if (files.Count == 0)
        {
            evidence.Add($"{source.Name} 是空的，或者里面的内容这次没有抓到");
            return Build(source, "multi_file_set", new JsonObject(), 20,
                $"{source.Name} 下没有看到任何文件", evidence, [], []);
        }

        var byExtension = files
            .GroupBy(f => Extension(f.Name), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();
        var largest = byExtension[0];

        // 同一种扩展名堆了好几个 —— 每次备份往同一个目录里丢一个文件，只关心最新那个。
        if (largest.Count() >= 3)
        {
            evidence.Add($"{source.Name} 里有 {files.Count} 个文件，其中 {largest.Count()} 个是 {largest.Key} 文件");
            var newest = files.OrderByDescending(f => f.LastModifiedAt ?? DateTime.MinValue).First();
            evidence.Add($"最新的是 {newest.Name}");

            var config = new JsonObject();
            if (!string.IsNullOrEmpty(largest.Key))
                config["includePatterns"] = new JsonArray($"*{largest.Key}");

            return Build(source, "latest_single_file", config, 78,
                $"{source.Name} 里堆着 {files.Count} 个备份文件，每次取最新的那个（最新：{newest.Name}）",
                evidence, [], []);
        }

        // 几个文件凑成一份备份。
        evidence.Add($"{source.Name} 里有 {files.Count} 个文件：{PreviewNames(files)}");
        var required = files.Count <= 6
            ? files.Select(f => new RequiredFileCandidateDto
            {
                Pattern = f.Name,
                PresentIn = 1,
                TotalUnits = 1,
                Recommended = true,
                TypicalSizeBytes = f.SizeBytes
            }).ToList()
            : [];

        var setConfig = new JsonObject { ["recursive"] = false };
        AddRequired(setConfig, required);
        return Build(source, "multi_file_set", setConfig, required.Count > 0 ? 68 : 45,
            $"{source.Name} 里有 {files.Count} 个文件，凑齐算一份完整备份" + DescribeRequired(required),
            evidence, required, [source]);
    }

    /// <summary>
    /// 从若干个"备份目录"里找出每次都出现的文件。
    ///
    /// 先按文件名精确统计；如果文件名里带日期（db_20260825.bak 这类），
    /// 没有任何文件名能重复出现，就退一步按扩展名统计。
    /// </summary>
    private static List<RequiredFileCandidateDto> InferRequiredFiles(
        IReadOnlyList<SnapshotNode> leaves, List<string> evidence)
    {
        if (leaves.Count == 0)
            return [];

        var leafFiles = leaves
            .Select(leaf => VisibleFiles(leaf).ToList())
            .Where(list => list.Count > 0)
            .ToList();
        if (leafFiles.Count == 0)
            return [];

        var byName = CountAcross(leafFiles, f => f.Name);
        var candidates = BuildCandidates(byName, leafFiles.Count, exact: true);

        if (candidates.Count == 0)
        {
            var byExtension = CountAcross(leafFiles, f => Extension(f.Name) is { Length: > 0 } ext ? "*" + ext : f.Name);
            candidates = BuildCandidates(byExtension, leafFiles.Count, exact: false);
            if (candidates.Count > 0)
                evidence.Add("文件名每次都不一样（带日期），改按扩展名认");
        }

        if (candidates.Count > 0)
        {
            var names = string.Join("、", candidates.Where(c => c.Recommended).Select(c => c.Pattern));
            if (!string.IsNullOrEmpty(names))
                evidence.Add($"考察的 {leafFiles.Count} 个备份目录里，每一个都有：{names}");

            var partial = candidates.Where(c => !c.Recommended).ToList();
            foreach (var item in partial)
                evidence.Add($"{item.Pattern} 只出现在 {item.PresentIn}/{item.TotalUnits} 个备份目录里，没有默认勾选");
        }

        return candidates;
    }

    private static Dictionary<string, (int Count, long Size)> CountAcross(
        List<List<SnapshotNode>> leafFiles, Func<SnapshotNode, string> keySelector)
    {
        var result = new Dictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var files in leafFiles)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var key = keySelector(file);
                if (!seen.Add(key))
                    continue;
                var size = file.SizeBytes;
                result[key] = result.TryGetValue(key, out var existing)
                    ? (existing.Count + 1, Math.Max(existing.Size, size))
                    : (1, size);
            }
        }

        return result;
    }

    private static List<RequiredFileCandidateDto> BuildCandidates(
        Dictionary<string, (int Count, long Size)> counts, int totalUnits, bool exact)
    {
        // 只在多个单元之间才谈"每次都出现"；单个单元时无从判断稳定性，
        // 但仍把文件列出来供人勾选——那是他自己的目录，他认得。
        var floor = totalUnits == 1 ? 1 : Math.Max(2, (int)Math.Ceiling(totalUnits * 0.5));
        var candidates = counts
            .Where(kv => kv.Value.Count >= floor)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.Size)
            .Take(12)
            .Select(kv => new RequiredFileCandidateDto
            {
                Pattern = kv.Key,
                PresentIn = kv.Value.Count,
                TotalUnits = totalUnits,
                TypicalSizeBytes = kv.Value.Size,
                Recommended = totalUnits == 1 || kv.Value.Count >= Math.Ceiling(totalUnits * RequiredThreshold)
            })
            .ToList();

        // 精确文件名模式下，如果一个都没达到推荐线，说明文件名不稳定，交给扩展名兜底。
        if (exact && !candidates.Any(c => c.Recommended))
            return [];

        return candidates;
    }

    private static void AddRequired(JsonObject config, IReadOnlyList<RequiredFileCandidateDto> required)
    {
        var recommended = required.Where(c => c.Recommended).Select(c => c.Pattern).ToList();
        if (recommended.Count == 0)
            return;

        var array = new JsonArray();
        foreach (var name in recommended)
            array.Add(name);
        config["requiredFiles"] = array;
    }

    private static string DescribeRequired(IReadOnlyList<RequiredFileCandidateDto> required)
    {
        var recommended = required.Where(c => c.Recommended).Select(c => c.Pattern).ToList();
        if (recommended.Count == 0)
            return "";
        return $"；每次的备份目录里固定有 {recommended.Count} 个文件：{string.Join("、", recommended)}";
    }

    private static RecognizerProposalDto Build(
        SnapshotNode source,
        string recognizerType,
        JsonObject config,
        int confidence,
        string summary,
        List<string> evidence,
        IReadOnlyList<RequiredFileCandidateDto> required,
        IReadOnlyList<SnapshotNode> leaves)
    {
        // 快照本身没抓全时，推断依据就是不完整的，置信度必须相应下调并说明。
        if (source.HasGaps)
        {
            confidence = Math.Max(20, confidence - 20);
            evidence.Add("这个目录没有被完整抓取（层数或条目数触顶），以上结论只覆盖看得见的部分");
        }

        return new RecognizerProposalDto
        {
            SourcePath = source.FullPath,
            RecognizerType = recognizerType,
            RecognizerConfig = config.ToJsonString(ConfigJson),
            Confidence = confidence,
            Summary = summary,
            Evidence = evidence,
            RequiredFileCandidates = required.ToList(),
            SuggestedApplicationName = GuessApplication(leaves)
        };
    }

    /// <summary>从文件特征猜应用名。猜不出就返回 null，让人自己填——瞎猜一个名字没有价值。</summary>
    private static string? GuessApplication(IReadOnlyList<SnapshotNode> leaves)
    {
        var names = leaves
            .SelectMany(VisibleFiles)
            .Select(f => f.Name.ToLowerInvariant())
            .ToList();
        if (names.Count == 0)
            return null;

        if (names.Any(n => n.StartsWith("ufdata")) || names.Any(n => n.EndsWith(".lst") && names.Any(x => x.EndsWith(".bak"))))
            return "用友 U8";
        if (names.Any(n => n.EndsWith(".trn")) && names.Any(n => n.EndsWith(".bak")))
            return "SQL Server";
        if (names.Any(n => n.EndsWith(".dmp")) || names.Any(n => n.EndsWith(".dbf")))
            return "Oracle";
        if (names.Any(n => n.EndsWith(".bak")))
            return "SQL Server";
        if (names.Any(n => n.EndsWith(".sql") || n.EndsWith(".sql.gz")))
            return "MySQL";
        return null;
    }

    private static bool HasDateChildren(SnapshotNode directory)
    {
        var children = VisibleDirectories(directory).ToList();
        if (children.Count == 0)
            return false;
        var dateChildren = children.Count(c => LooksLikeDate(c.Name));
        return (double)dateChildren / children.Count >= DateLayerThreshold;
    }

    /// <summary>
    /// 目录名是否像一个日期或时间戳。
    ///
    /// 判据是"抽掉非数字后能不能读成一个合理的日期"，而不是穷举格式：
    /// 20260825、2026-08-25、2026_08_25、20260825_0136、财务20260825 都要认出来，
    /// 而 ZT001、账套001 这类必须认不出来（它们只有 3 位数字）。
    /// </summary>
    public static bool LooksLikeDate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (digits.Length is < 6 or > 20)
            return false;

        // 名字里字母（含中文）太多就不是日期目录了，是带编号的业务名。
        if (name.Count(char.IsLetter) > 8)
            return false;

        if (digits.Length >= 8
            && int.TryParse(digits.AsSpan(0, 4), out var year)
            && int.TryParse(digits.AsSpan(4, 2), out var month)
            && int.TryParse(digits.AsSpan(6, 2), out var day)
            && year is >= 1990 and <= 2100
            && month is >= 1 and <= 12
            && day is >= 1 and <= 31)
        {
            return true;
        }

        if (digits.Length is 6 or 7
            && int.TryParse(digits.AsSpan(0, 2), out _)
            && int.TryParse(digits.AsSpan(2, 2), out var shortMonth)
            && int.TryParse(digits.AsSpan(4, 2), out var shortDay)
            && shortMonth is >= 1 and <= 12
            && shortDay is >= 1 and <= 31)
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<SnapshotNode> VisibleDirectories(SnapshotNode node) =>
        node.Directories.Where(d => !d.IsHidden && !d.Name.StartsWith('.'));

    private static IEnumerable<SnapshotNode> VisibleFiles(SnapshotNode node) =>
        node.Files.Where(f => !f.IsHidden
                              && !f.Name.StartsWith('~')
                              && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                              && !f.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));

    private static string Extension(string name)
    {
        var index = name.LastIndexOf('.');
        return index <= 0 ? string.Empty : name[index..];
    }

    private static string PreviewNames(IReadOnlyList<SnapshotNode> nodes)
    {
        var names = nodes.Take(3).Select(n => n.Name).ToList();
        return nodes.Count > 3
            ? string.Join("、", names) + $" … 共 {nodes.Count} 个"
            : string.Join("、", names);
    }
}
