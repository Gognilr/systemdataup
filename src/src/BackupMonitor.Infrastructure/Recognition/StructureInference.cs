using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Recognition;

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
    /// 一个目录要被判定为"一份备份由多个同名文件构成"，需要这个比例的 basename
    /// 拥有 ≥2 个不同扩展名。
    /// </summary>
    private const double PairedFileThreshold = 0.6;

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

        // 情形零：纯数字分层（年 \ 月）。致远 OA 的 Backup\2026\08\ 就是这一种。
        //
        // 排在情形一之前是因为它更具体——要求年、月两层同时成立，先判不会抢走原有分支；
        // 不成立时原样落回下面，连 evidence 都不留（它自己攒一份，成了才交出去）。
        //
        // 这里是**新增**一条识别，而不是放宽 LooksLikeDate 的 6 位数字下限：
        // 那个下限正是用来把 ZT001、账套001 挡在外面的，放宽会引入更糟的误判。
        var yearDirectories = directories.Where(d => LooksLikeYear(d.Name)).ToList();
        var yearRatio = (double)yearDirectories.Count / directories.Count;
        if (yearRatio >= DateLayerThreshold)
        {
            var numeric = InferNumericLayers(source, yearDirectories, yearRatio);
            if (numeric is not null)
                return numeric;
        }

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

        // 情形二：下面是业务单元，单元里按日期建目录 —— 账套001\20260825\…
        //
        // 这里刻意不数「第一层里有几个带日期子目录」，而是先把**单元集合**解出来。
        // 原因是单元不一定都在同一层：
        //
        //   D:\自动备份\
        //     ├── ZT001\20260822\…          ← 单元在第 1 层
        //     └── ZT201-ZT216\ZT201\20260828\…  ← 单元在第 2 层，中间那层只是分组容器
        //
        // 按第一层数的话，2/3 的目录带日期子目录，勉强过线，于是 ZT201-ZT216 被当成
        // 一个单元：摘要说「3 个业务单元」、必需文件只考察了 2 个 leaf、预演又列出 3 个，
        // 同一份推断里三个数字来自三段独立代码。落地之后更糟——扫描时那个「单元」
        // 取到的是 ZT216，把它底下 7 天的文件混成一份备份判 passed，
        // 而 ZT201–ZT215 这 15 个账套压根不在监控范围内，且没有任何提示。
        //
        // 所以单元集合是这一分支唯一的事实来源：摘要说几个、必需文件考察几个、
        // 预演跑几个，全部由它派生，结构上不可能再对不上。
        var units = ResolveUnits(source);
        var unitsWithDates = units.Where(u => u.HasDateChildren).ToList();
        var unitDateRatio = units.Count == 0 ? 0 : (double)unitsWithDates.Count / units.Count;

        if (unitDateRatio >= DateLayerThreshold)
        {
            var leaves = unitsWithDates.Select(u => u.Leaf).ToList();
            var depths = units.Select(u => u.Depth).Distinct().OrderBy(d => d).ToList();
            var containers = units
                .Where(u => u.Depth > 1)
                .Select(u => u.RelativePath[..u.RelativePath.LastIndexOf('/')])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            evidence.Add($"{source.Name} 下一共找到 {units.Count} 个业务单元（{PreviewNames(units.Select(u => u.Node).ToList())}）");
            if (containers.Count > 0)
            {
                evidence.Add(
                    $"{string.Join("、", containers)} 自己不是业务单元，只是把单元分了组——"
                    + $"真正的单元是它下面那一层，共 {units.Count(u => u.Depth > 1)} 个");
            }

            evidence.Add($"其中 {unitsWithDates.Count} 个的下一层是日期目录，结构彼此一致");
            var newestLeaf = leaves.OrderByDescending(l => l.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (newestLeaf is not null)
                evidence.Add($"最新的日期目录是 {newestLeaf.Name}");

            var required = InferRequiredFiles(leaves, evidence);

            // 单元都在同一层时把深度写死，只有层级不齐才用自适应。
            // 能说死的就说死：写死的深度不会猜错，date_leaf 会。
            var config = new JsonObject();
            if (depths.Count == 1)
            {
                config["businessUnitDepth"] = depths[0];
                config["unitLayout"] = "latest_directory";
            }
            else
            {
                config["unitLayout"] = "date_leaf";
            }

            AddRequired(config, required);

            var layoutNote = depths.Count == 1
                ? ""
                : $"（单元分布在第 {string.Join("、", depths)} 层，按结构自动认，不按固定层数）";

            return Build(
                source, "subdirectory_units", config,
                confidence: unitDateRatio >= 0.9 ? 92 : 74,
                summary: $"{source.Name} 下有 {units.Count} 个业务单元（{PreviewNames(units.Select(u => u.Node).ToList())}）{layoutNote}，"
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

        // 先问一句"一份备份是不是由同名的多个文件构成"——这一步必须排在按扩展名分组之前。
        //
        // 致远 OA 的 2026-08-17@02_00.zip + 同名 .properties 就是这种结构：两种扩展名各 30 个，
        // 按数量分组是平局，谁排第一取决于文件枚举顺序（名字序，p 在 z 前）。只挑一个写进
        // includePatterns 的后果被 RecognizerRules.IsAllowedFile 放大成：所有 zip 被彻底排除，
        // 不扫描、不哈希、不上传、不参与缺失判定；而 .properties 每天准时出现，任务天天判 passed。
        // zip 是 0 字节、压缩中断、磁盘满只写了元数据——这些真正的故障一个都不会告警。
        // 这是一个备份监控系统能给出的最危险的那种错误答案，所以成对结构必须先于扩展名判定。
        var paired = DetectPairedFiles(files);
        if (paired is not null)
        {
            evidence.Add($"{source.Name} 里有 {files.Count} 个文件，按去掉扩展名的文件名可以分成 {paired.GroupCount} 组");
            evidence.Add($"其中 {paired.PairedGroupCount} 组由多个同名文件构成，每组包含 {string.Join("、", paired.Extensions)}");
            evidence.Add($"最新的一组是 {paired.NewestGroupName}（{PreviewNames(paired.NewestGroupFiles)}）");

            // includePatterns 这里刻意不写。写全部扩展名与不写在本结构上等价，
            // 而一旦有人日后"顺手精简"成单个扩展名，就会退回上面那个静默缺陷。
            // groupBy 与 requiredFiles 是一对：requiredFiles 保证 zip 不被排除，
            // groupBy 保证一个月的 30 组不被算成一份（manifest 每天变、每次扫描
            // 把 30 个 zip 全读一遍算 SHA-256）。少任何一半，OA 这类结构都还是错的。
            var pairedConfig = new JsonObject
            {
                ["recursive"] = false,
                ["groupBy"] = "basename"
            };
            AddRequired(pairedConfig, paired.Candidates);

            return Build(
                source, "multi_file_set", pairedConfig,
                confidence: paired.Ratio >= 0.9 ? 80 : 68,
                summary: $"{source.Name} 里每份备份由 {paired.Extensions.Count} 个同名文件组成"
                         + $"（{string.Join("、", paired.Extensions)}），共 {paired.GroupCount} 组，"
                         + $"每次只认最新的一组：{paired.NewestGroupName}",
                evidence, paired.Candidates, [source]);
        }

        // 再问一句"是不是每天一组、每组好几个库"——排在扩展名判定之前，理由同上。
        //
        // D:\数据库备份\ 是这一种：没有日期子目录，14 个 .bak 全平铺着，日期写在文件名里。
        //   beeServer_backup_2026_08_22_000004_2256969.bak      5 MB
        //   dzwl_product_backup_2026_08_22_000004_4679193.bak   1.5 GB
        // 同一天的两个文件才构成一次完整备份，14 个 = 7 天 × 2 个库。
        //
        // 按 basename 分组认不出来（14 个名字互不相同），落到扩展名分支就成了
        // "堆着 14 个 .bak，取最新的那个"——而 latest_single_file 又会把整个目录收进来，
        // 于是每次扫描把 7 天的历史全算一遍哈希、每次上传 10.59GB 而不是 1.5GB，
        // 且"今天某个库没转储出来"永远不会被发现。
        var dated = DetectDatedFileSets(files);
        if (dated is not null)
        {
            evidence.Add($"{source.Name} 里有 {files.Count} 个文件，文件名里都带日期，按日期可以分成 {dated.GroupCount} 组");
            evidence.Add($"每组固定由 {dated.Patterns.Count} 个文件构成：{FileNamePattern.Describe(dated.Patterns)}");
            evidence.Add($"最新的一组是 {dated.NewestGroupKey}（{PreviewNames(dated.NewestGroupFiles)}）");

            var datedConfig = new JsonObject
            {
                ["recursive"] = false,
                ["groupBy"] = dated.GroupRegex
            };
            AddRequired(datedConfig, dated.Candidates);

            return Build(
                source, "multi_file_set", datedConfig,
                confidence: dated.Ratio >= 0.9 ? 82 : 70,
                summary: $"{source.Name} 里每天一组备份，每组 {dated.Patterns.Count} 个"
                         + $"（{FileNamePattern.Describe(dated.Patterns)}），共 {dated.GroupCount} 组，"
                         + $"每次只认最新的一组：{dated.NewestGroupKey}",
                evidence, dated.Candidates, [source]);
        }

        var byExtension = files
            .GroupBy(f => Extension(f.Name), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            // 平局时按总字节数决胜。数量相同的两种扩展名里，数据包和元数据的体积差
            // 四到五个数量级，一比就分得出来；不加这一手，胜负取决于枚举顺序。
            .ThenByDescending(g => g.Sum(f => f.SizeBytes))
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
    /// 纯数字分层：源目录 → 年 → 月 → 文件。
    ///
    /// 任一层不成立就返回 null，由调用方落回既有分支——这里不硬撑，
    /// 也不把攒到一半的 evidence 交出去（那会让人看到一堆与结论无关的观察）。
    /// </summary>
    private static RecognizerProposalDto? InferNumericLayers(
        SnapshotNode source, List<SnapshotNode> yearDirectories, double yearRatio)
    {
        var evidence = new List<string>();
        // 与下面挑"最新的月"同一条规则：先看有没有内容，再按名字。
        // 只按名字的话，元旦刚建出来还空着的 2027 会顶掉真正有备份的 2026，
        // 于是向导对一个它本来看得懂的结构回一句"没能看懂"——而扫描端的通配展开
        // 按内容时间挑，根本不会选中那个空目录。两边对"最新"的判断不该在这里岔开。
        var latestYear = yearDirectories
            .OrderByDescending(d => VisibleDirectories(d).Any())
            .ThenByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        evidence.Add(
            $"{source.Name} 下有 {yearDirectories.Count} 个年份目录（{PreviewNames(yearDirectories)}），"
            + $"最新的是 {latestYear.Name}");

        var monthCandidates = VisibleDirectories(latestYear).ToList();
        if (monthCandidates.Count == 0)
            return null;

        var monthDirectories = monthCandidates.Where(d => LooksLikeMonthOrDay(d.Name)).ToList();
        var monthRatio = (double)monthDirectories.Count / monthCandidates.Count;
        if (monthRatio < DateLayerThreshold)
            return null;

        // 服务器通常只保留一个月，旧月份目录会被清空或删掉。所以"最新的月"先看有没有内容，
        // 再按名字——只按名字的话，一个刚建出来还没写东西的空目录会顶掉真正有备份的那个。
        var latestMonth = monthDirectories
            .OrderByDescending(d => VisibleFiles(d).Any())
            .ThenByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        evidence.Add(
            $"{latestYear.Name} 下有 {monthDirectories.Count} 个月份目录（{PreviewNames(monthDirectories)}），"
            + $"最新的是 {latestMonth.Name}");

        var monthFiles = VisibleFiles(latestMonth).ToList();
        if (monthFiles.Count == 0)
        {
            evidence.Add($"{latestMonth.Name} 里这次没有看到文件");
            return null;
        }

        var config = new JsonObject { ["recursive"] = false };
        List<RequiredFileCandidateDto> required;
        string groupSummary;

        // 分组方案复用 R1 那套成对检测：一份备份由同名的多个文件构成时，
        // 每天一组、只认最新的一组，靠 groupBy 表达。
        var paired = DetectPairedFiles(monthFiles);
        if (paired is not null)
        {
            config["groupBy"] = "basename";
            required = paired.Candidates;
            evidence.Add(
                $"{latestMonth.Name} 里有 {paired.GroupCount} 组同名文件，"
                + $"每组包含 {string.Join("、", paired.Extensions)}");
            evidence.Add($"每次只认最新的一组：{paired.NewestGroupName}");
            groupSummary = $"每天一组（{string.Join("、", paired.Extensions)}），每次只看最新的那一组";
        }
        else
        {
            required = InferRequiredFiles([latestMonth], evidence);
            var newest = monthFiles.OrderByDescending(f => f.LastModifiedAt ?? DateTime.MinValue).First();
            evidence.Add($"{latestMonth.Name} 里有 {monthFiles.Count} 个文件，最新的是 {newest.Name}");
            groupSummary = $"{latestMonth.Name} 里的 {monthFiles.Count} 个文件凑齐算一份";
        }

        AddRequired(config, required);

        // 源路径写成通配，任务才能跨月、跨年自动跟随；写死到 08 的话，
        // 到了 9 月表现是 path_not_found，需要有人记得去改。
        var wildcardSource = $"{source.FullPath}/*/*";

        // 三层都清晰才给 85；任一层的比例落在 60%–90% 之间说明结构不完全规整，给 70。
        // HasGaps 的减 20 由 Build 统一处理，不在这里另开一套。
        var fullyRegular = yearRatio >= 0.9
                           && monthRatio >= 0.9
                           && paired is not null
                           && paired.Ratio >= 0.9;

        return Build(
            source, "multi_file_set", config,
            confidence: fullyRegular ? 85 : 70,
            summary: $"{source.Name} 下按年、月分层，最新的是 {latestYear.Name}\\{latestMonth.Name}；"
                     + groupSummary
                     + $"；源路径写成 {wildcardSource.Replace('/', '\\')}，跨月、跨年自动跟随",
            evidence, required, [latestMonth],
            sourcePathOverride: wildcardSource);
    }

    /// <summary>
    /// 日期名判定已下沉到 Shared.Recognition.DateName，让 Agent 也能执行同一套语义。
    /// 这里留三个转发是给既有调用点和测试用的，判定本身只有那一份。
    /// </summary>
    public static bool LooksLikeYear(string? name) => DateName.LooksLikeYear(name);

    public static bool LooksLikeMonthOrDay(string? name) => DateName.LooksLikeMonthOrDay(name);

    public static bool LooksLikeDate(string name) => DateName.LooksLikeDate(name);

    /// <summary>
    /// 一个目录里"成对文件"的观察结果：一份备份由同名的若干个文件构成。
    /// 不成立时 DetectPairedFiles 返回 null。
    /// </summary>
    private sealed record PairedFileSet(
        int GroupCount,
        int PairedGroupCount,
        double Ratio,
        IReadOnlyList<string> Extensions,
        List<RequiredFileCandidateDto> Candidates,
        string NewestGroupName,
        IReadOnlyList<SnapshotNode> NewestGroupFiles);

    /// <summary>
    /// 判断"一份备份由同名的多个文件构成"：按去掉扩展名的文件名分组，
    /// 若 <see cref="PairedFileThreshold"/> 以上的组拥有 ≥2 个不同扩展名即成立。
    ///
    /// 判不成立就返回 null，交回按扩展名的老路径——宁可认不出来交给人选，
    /// 也不要认错（本文件头部的既定原则）。两处刻意收紧：
    /// 只有一组时"分组"没有意义（那就是几个文件凑一份备份，按精确文件名点名更准）；
    /// 没有任何扩展名达到推荐线时同样不成立，否则会产出一份谁都不必需的 requiredFiles。
    /// </summary>
    private static PairedFileSet? DetectPairedFiles(IReadOnlyList<SnapshotNode> files)
    {
        if (files.Count == 0)
            return null;

        var groups = files
            .GroupBy(f => BaseName(f.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count < 2)
            return null;

        var paired = groups.Where(g => DistinctExtensions(g).Count >= 2).ToList();
        var ratio = (double)paired.Count / groups.Count;
        if (ratio < PairedFileThreshold)
            return null;

        // 扩展名的"每组都出现"用 RequiredThreshold，与 InferRequiredFiles 同一把尺子。
        var stats = new Dictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in paired)
        {
            foreach (var extension in DistinctExtensions(group))
            {
                var size = group
                    .Where(f => string.Equals(Extension(f.Name), extension, StringComparison.OrdinalIgnoreCase))
                    .Max(f => f.SizeBytes);
                stats[extension] = stats.TryGetValue(extension, out var existing)
                    ? (existing.Count + 1, Math.Max(existing.Size, size))
                    : (1, size);
            }
        }

        var candidates = stats
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.Size)
            .Select(kv => new RequiredFileCandidateDto
            {
                Pattern = "*" + kv.Key,
                PresentIn = kv.Value.Count,
                TotalUnits = paired.Count,
                TypicalSizeBytes = kv.Value.Size,
                Recommended = kv.Value.Count >= Math.Ceiling(paired.Count * RequiredThreshold)
            })
            .ToList();
        if (!candidates.Any(c => c.Recommended))
            return null;

        // 时间相同时按组名倒序，保证结果稳定而不依赖枚举顺序——R1 的教训就是这个。
        var newest = groups
            .OrderByDescending(g => g.Max(f => f.LastModifiedAt ?? DateTime.MinValue))
            .ThenByDescending(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .First();

        return new PairedFileSet(
            groups.Count,
            paired.Count,
            ratio,
            candidates.Select(c => c.Pattern).ToList(),
            candidates,
            newest.Key,
            newest.ToList());
    }

    /// <summary>
    /// 一个平铺目录里「每天一组、每组若干个库」的观察结果。不成立时 DetectDatedFileSets 返回 null。
    /// </summary>
    private sealed record DatedFileSet(
        int GroupCount,
        double Ratio,
        IReadOnlyList<string> Patterns,
        string GroupRegex,
        List<RequiredFileCandidateDto> Candidates,
        string NewestGroupKey,
        IReadOnlyList<SnapshotNode> NewestGroupFiles);

    /// <summary>
    /// 判断「一次备份 = 同一天的那几个文件」：按文件名里的日期分组，用归一化模式描述组成员。
    ///
    /// 四道关，任何一道不过就返回 null 交回原路径——宁可认不出来交给人选，也不要认错：
    ///
    ///   1. 每个文件都要能取到日期，取不到的说明这批文件不是这个结构；
    ///   2. 至少两组，一组谈不上"分组"；
    ///   3. 多数组的成员模式集合要一致，否则每天备的东西都不一样，凑不出"一次完整备份"的定义；
    ///   4. 模式必须 ≥2 个。只有 1 个模式是"单库每天一份"，那是 latest_single_file 的场景，
    ///      在这里认下来会把既有行为改坏。
    ///
    /// 最后还要把生成的 groupBy 正则在样本上验一遍。不验的后果不是报错而是**静默退化**：
    /// RecognizerRules.GroupKeyOf 对匹配不上的文件是"归入未分组并保留"，
    /// 一条写坏的正则会安静地等价于不分组，回到今天这个错误结果。
    /// </summary>
    private static DatedFileSet? DetectDatedFileSets(IReadOnlyList<SnapshotNode> files)
    {
        if (files.Count < 2)
            return null;

        var tokens = files
            .Select(f => (File: f, Token: FileNamePattern.ExtractDateToken(f.Name)))
            .ToList();
        if (tokens.Any(t => t.Token is null))
            return null;

        var groups = tokens
            .GroupBy(t => t.Token!, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count < 2)
            return null;

        var patternSets = groups.ToDictionary(
            g => g.Key,
            g => g.Select(x => FileNamePattern.Normalize(x.File.Name))
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                  .ToList(),
            StringComparer.OrdinalIgnoreCase);

        // 出现得最多的那一套成员模式就是"每次备份应该长什么样"。
        // 平局时按模式数多的优先，再按字典序——保证结果稳定而不依赖枚举顺序（R1 的教训）。
        var dominant = patternSets.Values
            .GroupBy(set => string.Join('|', set), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.First().Count)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .First();

        var ratio = (double)dominant.Count() / groups.Count;
        if (ratio < PairedFileThreshold)
            return null;

        var patterns = dominant.First();
        if (patterns.Count < 2)
            return null;

        var groupRegex = FileNamePattern.BuildDateGroupRegex(files[0].Name);
        if (groupRegex is null || !FileNamePattern.Validate(groupRegex, files.Select(f => f.Name)))
            return null;

        var threshold = (int)Math.Ceiling(groups.Count * RequiredThreshold);
        var candidates = patterns
            .Select(pattern =>
            {
                var matching = files
                    .Where(f => string.Equals(FileNamePattern.Normalize(f.Name), pattern, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var presentIn = patternSets.Count(kv => kv.Value.Contains(pattern, StringComparer.OrdinalIgnoreCase));
                var perGroup = groups
                    .Select(g => g.Count(x => string.Equals(
                        FileNamePattern.Normalize(x.File.Name), pattern, StringComparison.OrdinalIgnoreCase)))
                    .Where(count => count > 0)
                    .ToList();

                return new RequiredFileCandidateDto
                {
                    Pattern = pattern,
                    Kind = "pattern",
                    PresentIn = presentIn,
                    TotalUnits = groups.Count,
                    CountPerUnitMin = perGroup.Count == 0 ? 0 : perGroup.Min(),
                    CountPerUnitMax = perGroup.Count == 0 ? 0 : perGroup.Max(),
                    TypicalSizeBytes = matching.Count == 0 ? 0 : matching.Max(f => f.SizeBytes),
                    Recommended = presentIn >= threshold,
                    NotRecommendedReason = presentIn >= threshold
                        ? null
                        : $"只出现在 {presentIn}/{groups.Count} 组里"
                };
            })
            .OrderByDescending(c => c.TypicalSizeBytes)
            .ToList();

        if (!candidates.Any(c => c.Recommended))
            return null;

        // 时间相同时按组名倒序，保证结果稳定而不依赖枚举顺序。
        var newest = groups
            .OrderByDescending(g => g.Max(x => x.File.LastModifiedAt ?? DateTime.MinValue))
            .ThenByDescending(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .First();

        return new DatedFileSet(
            groups.Count,
            ratio,
            patterns,
            groupRegex,
            candidates,
            newest.Key,
            newest.Select(x => x.File).ToList());
    }

    private static List<string> DistinctExtensions(IEnumerable<SnapshotNode> files) =>
        files.Select(f => Extension(f.Name))
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>去掉扩展名的文件名。边界与 Extension 一致：前导点的文件不算有扩展名。</summary>
    private static string BaseName(string name)
    {
        var index = name.LastIndexOf('.');
        return index <= 0 ? name : name[..index];
    }

    /// <summary>
    /// 从若干个「备份目录」里找出每次都出现的文件，分三档给出。
    ///
    /// 原先只有两档：**精确比文件名**，或者（精确名一个都没达标时）退一步**比扩展名**。
    /// 中间那一档缺失，后果是名字带年份、序号的文件在候选里直接消失——
    /// 用友 U8 的 UFFile_001_2022.dat…UFFile_001_2026.dat 在界面上连一行提示都看不到，
    /// 使用者盯着这几个文件，得不到任何关于它们算不算数的说法。
    ///
    /// 三档：
    ///   1. 固定名必需  —— 精确文件名在 ≥90% 的单元里出现（UFDATA.BAK）
    ///   2. 模式必需    —— 归一化后的模式在 ≥90% 的单元里出现，**且各单元里的个数一致**
    ///   3. 附带文件    —— 其余，列出来但不勾选，并说明为什么
    ///
    /// 第 2 档那个「个数一致」的条件是这套判断的关键，来自一条领域事实：
    /// U8 的 UFFile 是按**年度**分的附件库，各账套的**启用年度不同**——
    /// 2022 年起用的账套有 5 个，2024 年起用的只有 3 个，两者都是完整备份。
    /// 个数在单元之间波动是这个结构的正常形态，不是缺失信号，拿它当判据只会制造假警报。
    /// 反过来，个数一致的模式（每天恰好一个的 *_backup_*.bak）才是可靠的判据：
    /// 少一个就是真的少了一个库。
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

        var totalUnits = leafFiles.Count;
        var threshold = totalUnits == 1 ? 1 : (int)Math.Ceiling(totalUnits * RequiredThreshold);

        // ── 第一档：精确文件名 ──
        var byName = CountAcross(leafFiles, f => f.Name);
        var exact = byName
            .Where(kv => kv.Value.Count >= threshold)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.Size)
            .Select(kv => new RequiredFileCandidateDto
            {
                Pattern = kv.Key,
                Kind = "exact",
                PresentIn = kv.Value.Count,
                TotalUnits = totalUnits,
                TypicalSizeBytes = kv.Value.Size,
                CountPerUnitMin = 1,
                CountPerUnitMax = 1,
                Recommended = true
            })
            .ToList();

        var exactNames = exact.Select(c => c.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── 第二档：归一化模式，只考察没被第一档盖住的文件 ──
        var patterns = CountPatterns(leafFiles, exactNames);
        var pattern = patterns
            .OrderByDescending(kv => kv.Value.PresentIn)
            .ThenByDescending(kv => kv.Value.Size)
            .Select(kv =>
            {
                var stable = kv.Value.CountMin == kv.Value.CountMax;
                var present = kv.Value.PresentIn >= threshold;
                return new RequiredFileCandidateDto
                {
                    Pattern = kv.Key,
                    Kind = "pattern",
                    PresentIn = kv.Value.PresentIn,
                    TotalUnits = totalUnits,
                    TypicalSizeBytes = kv.Value.Size,
                    CountPerUnitMin = kv.Value.CountMin,
                    CountPerUnitMax = kv.Value.CountMax,
                    Recommended = present && stable,
                    NotRecommendedReason = !present
                        ? $"只出现在 {kv.Value.PresentIn}/{totalUnits} 个备份目录里"
                        : stable
                            ? null
                            : $"各备份目录里的个数不一样（{kv.Value.CountMin}–{kv.Value.CountMax} 个），"
                              + "不能作为完整性判据；文件本身照常上传"
                };
            })
            .ToList();

        var candidates = exact.Concat(pattern).Take(16).ToList();

        var recommended = candidates.Where(c => c.Recommended).Select(c => c.Pattern).ToList();
        if (recommended.Count > 0)
            evidence.Add($"考察的 {totalUnits} 个备份目录里，每一个都有：{string.Join("、", recommended)}");

        foreach (var item in candidates.Where(c => !c.Recommended))
            evidence.Add($"{item.Pattern}：{item.NotRecommendedReason}，没有默认勾选");

        return candidates;
    }

    /// <summary>
    /// 按归一化模式统计：这个模式出现在多少个单元里，以及**每个单元里有几个**。
    /// 个数的最小/最大值是第二档能不能当判据的依据，见 InferRequiredFiles 的说明。
    /// </summary>
    private static Dictionary<string, (int PresentIn, int CountMin, int CountMax, long Size)> CountPatterns(
        List<List<SnapshotNode>> leafFiles, HashSet<string> alreadyCovered)
    {
        var result = new Dictionary<string, (int PresentIn, int CountMin, int CountMax, long Size)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var files in leafFiles)
        {
            var perUnit = new Dictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (alreadyCovered.Contains(file.Name))
                    continue;
                var key = FileNamePattern.Normalize(file.Name);
                perUnit[key] = perUnit.TryGetValue(key, out var seen)
                    ? (seen.Count + 1, Math.Max(seen.Size, file.SizeBytes))
                    : (1, file.SizeBytes);
            }

            foreach (var (key, value) in perUnit)
            {
                result[key] = result.TryGetValue(key, out var existing)
                    ? (existing.PresentIn + 1,
                       Math.Min(existing.CountMin, value.Count),
                       Math.Max(existing.CountMax, value.Count),
                       Math.Max(existing.Size, value.Size))
                    : (1, value.Count, value.Count, value.Size);
            }
        }

        return result;
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
        IReadOnlyList<SnapshotNode> leaves,
        string? sourcePathOverride = null)
    {
        // 快照本身没抓全时，推断依据就是不完整的，置信度必须相应下调并说明。
        if (source.HasGaps)
        {
            confidence = Math.Max(20, confidence - 20);
            evidence.Add("这个目录没有被完整抓取（层数或条目数触顶），以上结论只覆盖看得见的部分");
        }

        return new RecognizerProposalDto
        {
            // 抓得不全 + 没看懂 = 很可能只是深度不够，值得再抓深一点重试一次。
            // 抓得不全但结论已经清楚（置信度够高）时不必重来：多一次往返换不到新信息。
            NeedsDeeperScan = source.HasGaps && confidence < 70,
            SourcePath = sourcePathOverride ?? source.FullPath,
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

    /// <summary>
    /// 一个业务单元候选：目录本身、它相对源目录的路径（就是将来的 external_key）、
    /// 它在第几层、下面是不是日期目录、以及它这次的采集根（最新的日期目录）。
    /// </summary>
    private sealed record UnitCandidate(
        SnapshotNode Node, string RelativePath, int Depth, bool HasDateChildren, SnapshotNode Leaf);

    /// <summary>
    /// 把源目录下的业务单元解出来——**这一分支唯一的事实来源**。
    ///
    /// 定位规则与 Agent 真扫时共用 BusinessUnitResolver，所以「向导说有 18 个账套」
    /// 和「实际扫描出 18 个账套」是同一段代码算出来的，不可能分叉。
    ///
    /// 深度上限取 4：源目录 → 分组容器 → 单元 → 日期目录。再深的结构快照本身也抓不到
    /// （向导抓 4 层），继续下探只会在 DepthLimited 的空目录上打转。
    /// </summary>
    private static List<UnitCandidate> ResolveUnits(SnapshotNode source)
    {
        var rules = RecognizerRules.Empty() with { UnitLayout = "date_leaf" };
        return BusinessUnitResolver
            .Resolve(source, rules, SnapshotRecognizer.SnapshotNavigator(source), maxDepth: 4)
            .Select(unit =>
            {
                var children = VisibleDirectories(unit.Directory).ToList();
                var dateChildren = children
                    .Where(c => LooksLikeDate(c.Name))
                    .OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 判据与 BusinessUnitResolver 停下来的判据一致：**多数**子目录像日期。
                // 用「至少有一个」会把「10 个业务子目录里混进 1 个 20260825」也算成单元。
                var isDateLayer = DateName.IsDateLayer(children.Select(c => c.Name).ToList());

                return new UnitCandidate(
                    unit.Directory,
                    unit.RelativePath,
                    unit.Depth,
                    isDateLayer,
                    isDateLayer ? dateChildren[0] : unit.Directory);
            })
            .ToList();
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
