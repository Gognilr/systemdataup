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
    /// 在快照上找业务单元时下探的层数上限：源目录 → 分组容器 → 单元 → 日期目录。
    ///
    /// 取 4 而不是 BusinessUnitResolver.DefaultMaxDepth（6），是因为向导默认只抓 4 层，
    /// 再深的结构快照里本来就不存在，继续下探只会在 DepthLimited 的空目录上打转；
    /// 而抓取深度也不能随手调大——层数一深条目数是乘出来的，很容易顶到 3000 条上限，
    /// 反而让本来看得懂的结构被标成"没抓全"。
    ///
    /// 这个 4 只是**调用方没说抓了几层时**的兜底。向导在只差一层就能看懂时会用 6 重抓一次
    /// （见 recognizer-wizard.js 的 DEEP_SCAN_DEPTH），那一次的快照真有 6 层，
    /// 此时仍按 4 找单元，等于白重抓——深处的单元照样看不见。所以 Infer 接收
    /// snapshotMaxDepth，有值就用它。
    /// </summary>
    private const int SnapshotUnitMaxDepth = 4;

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

    /// <param name="snapshotMaxDepth">
    /// 本次快照实际抓取的层数（BrowseSnapshotDto.MaxDepth）。0 表示调用方不知道，
    /// 此时按 <see cref="SnapshotUnitMaxDepth"/> 兜底。它决定在快照上找业务单元时下探多深，
    /// 并会原样写进配置的 unitMaxDepth 交给预演和真扫——三方共用一个数，才谈得上一致。
    /// </param>
    public static RecognizerProposalDto Infer(SnapshotNode source, DateTime capturedAt, int snapshotMaxDepth = 0)
    {
        // 上限仍夹在 DefaultMaxDepth 以内：抓取深度是客户端传上来的，不该由它决定
        // 服务端递归多深。
        var unitMaxDepth = snapshotMaxDepth > 0
            ? Math.Min(snapshotMaxDepth, BusinessUnitResolver.DefaultMaxDepth)
            : SnapshotUnitMaxDepth;

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
                evidence, required, dateDirectories,
                dateNames: dateDirectories.Select(d => d.Name).ToList());
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
        var units = ResolveUnits(source, unitMaxDepth);
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

                // 把推断实际用过的下探层数一并写进配置。
                //
                // 不写的话，预演和 Agent 真扫都会各自取 BusinessUnitResolver.DefaultMaxDepth（6），
                // 而推断用的是 4。快照只有 4 层时预演无处可去、碰巧一致；Agent 面对的是真实
                // 文件系统，date_leaf 会一路下探到第 6 层，于是「向导说有这些账套」和
                // 「实际扫出这些账套」静默地不是同一批，external_key（相对路径）也跟着变。
                // depths.Count == 1 那条分支不需要它：那里写死了 businessUnitDepth，走的是固定深度。
                config["unitMaxDepth"] = unitMaxDepth;
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
                evidence, required, leaves,
                // 周期要从**一个**单元的日期目录序列上算：把所有单元的日期目录混在一起，
                // 18 个账套同一天各有一个目录，去重之后间隔没变、但「样本」凭空多了 18 倍，
                // 缺口判定会被同一天的重复项掩盖。取日期目录最多的那个单元最有代表性。
                dateNames: unitsWithDates
                    .Select(u => VisibleDirectories(u.Node).Where(c => LooksLikeDate(c.Name)).Select(c => c.Name).ToList())
                    .OrderByDescending(list => list.Count)
                    .FirstOrDefault() ?? []);
        }

        // 情形二点五：固定槽位轮转覆盖 —— 周一\ 周二\ … 周日\、Day1\…Day7\
        //
        // 排在情形三之前是因为它更具体（情形三只要求「多数子目录里直接有文件」，
        // 轮转结构必然满足），先判不会抢走原有分支；不成立返回 null 原样落回。
        var rotating = DetectRotatingSlots(source, directories);
        if (rotating is not null)
            return rotating;

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

    // ---------- B1：轮转式备份目录 ----------

    /// <summary>
    /// 轮转槽位词表。**就这四类，不要再扩。**
    ///
    /// 前三类词表封闭、语义唯一，名字本身就能自证；纯序号那一类靠「个数 ≥ 3、最大值 ≤ 31」
    /// 两个额外约束收窄，否则 1…18 这种账套编号会撞进来。
    ///
    /// 明确不收 `A B C`、`奇/偶`、`一 二 三`：它们与真实业务分组（车间 A/B/C、区域 A/B）
    /// 无法区分，误判代价高于收益。真遇到了走逃生门——人手动选 latest_directory 模板，
    /// 一次配置的事。
    /// </summary>
    private static readonly (string Label, string[] Words)[] RotationVocabularies =
    [
        ("星期（中文）", ["周一", "周二", "周三", "周四", "周五", "周六", "周日", "周天",
                        "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日", "星期天"]),
        ("星期（英文）", ["mon", "tue", "wed", "thu", "fri", "sat", "sun",
                        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]),
        ("DayN", ["day1", "day2", "day3", "day4", "day5", "day6", "day7",
                  "day01", "day02", "day03", "day04", "day05", "day06", "day07",
                  "d1", "d2", "d3", "d4", "d5", "d6", "d7"])
    ];

    /// <summary>轮转槽位至少要有这么多个——两个目录说明不了任何轮转规律。</summary>
    private const int MinRotationSlots = 3;

    /// <summary>
    /// 相邻槽位之间最小的时间间隔（天）。取 4 小时：比它更密的「均匀分布」
    /// 是同一批任务依次跑完的痕迹，不是轮转覆盖。
    /// </summary>
    private const double MinRotationIntervalDays = 4.0 / 24;

    /// <summary>
    /// 固定槽位轮转覆盖：`周一…周日`、`Day1…Day7`、`Mon…Sun`、`01…07`。
    ///
    /// 轮转的语义是**覆盖**：N 个槽位是同一份备份的 N 个历史副本，任一时刻只有一个是
    /// 「当前的那份备份」。判成 N 个业务单元的后果有三个，最要命的是第三个：
    ///   1. 单元数与 external_key 全错（周一…周日 成了业务单元名）；
    ///   2. 存储与带宽 ×N（N 个副本各自成为独立候选并各自上传）；
    ///   3. **周期性偏科失败完全看不见**——MissedBackupWorker 是按任务判的，
    ///      只要 N 个槽位里有任何一个是新的就不告警；而「周三」这个假单元自己的预检
    ///      永远 passed（文件在、必需文件齐、早过稳定窗口）。于是「每周三的备份一直没成功」
    ///      这件事，系统里没有任何一处会说出来。
    ///
    /// **不新增识别器类型**：轮转的结构语义与现有 latest_directory 完全一致，
    /// 而 Agent 侧的 latest_directory 是按「目录内最新文件的 mtime」选目录、不是按名字排序，
    /// 对 周一…周日 这种名字无序的槽位天然正确。所以这是一条纯推断端的改动。
    ///
    /// 两道判据必须**同时**成立，只用名字是不够的：一个恰好按星期分的**业务**目录
    /// 会被错认。宁可认不出来交给人选。
    /// </summary>
    private static RecognizerProposalDto? DetectRotatingSlots(SnapshotNode source, List<SnapshotNode> directories)
    {
        if (directories.Count < MinRotationSlots)
            return null;

        var vocabulary = MatchRotationVocabulary(directories.Select(d => d.Name).ToList());
        if (vocabulary is null)
            return null;

        // 时间判据（主判据）：各槽位「最新文件 mtime」明显错开，而不是聚集。
        // N 个账套是同一批跑的，最新时间聚集在同一天；N 个轮转槽位的最新时间
        // 大致等间隔铺开 N 个周期。任一槽位取不到 mtime 就整体不成立——
        // 缺一个点，「错开」这件事就没法证明，而没法证明时的正确答案是「不认」。
        var slotTimes = directories
            .Select(d => (Dir: d, Time: NewestFileTime(d)))
            .ToList();
        if (slotTimes.Any(x => x.Time is null))
            return null;

        var times = slotTimes.Select(x => x.Time!.Value).OrderBy(t => t).ToList();
        var spread = (times[^1] - times[0]).TotalDays;
        var intervals = new List<double>(times.Count - 1);
        for (var i = 1; i < times.Count; i++)
            intervals.Add((times[i] - times[i - 1]).TotalDays);

        var median = intervals.OrderBy(v => v).ElementAt(intervals.Count / 2);

        // 间隔必须有绝对下限，不能只看「是不是均匀铺开」。
        // 只比相对分布的话这条判据是尺度无关的：18 个账套在同一批里依次跑完，
        // 彼此相差几十秒到几分钟，同样是「均匀铺开 N 个间隔」，会被判成轮转。
        // 而轮转槽位按定义是**不同周期**写的——日轮转差一天、周轮转差一周，
        // 以小时计的下限足以把「同一批跑完」和「隔一个周期覆盖一次」分开。
        if (median < MinRotationIntervalDays)
            return null;

        if (spread < (directories.Count - 1) * median * 0.5)
            return null;

        var current = slotTimes.OrderByDescending(x => x.Time!.Value).First().Dir;
        var clean = spread >= (directories.Count - 1) * median * 0.8;

        var evidence = new List<string>
        {
            $"{source.Name} 下的 {directories.Count} 个目录（{PreviewNames(directories)}）"
            + $"名字全部落在同一个固定词表里：{vocabulary}，且互不重复",
            $"各目录里最新文件的时间依次错开（最早 {times[0]:yyyy-MM-dd HH:mm}、"
            + $"最晚 {times[^1]:yyyy-MM-dd HH:mm}，相邻间隔中位数约 {DescribeInterval(median)}），"
            + "说明每次只覆盖其中一个，而不是 N 个业务同时在备份",
            // 逃生门：自适应判定没有逃生门就是死局。
            "如果这其实是 " + directories.Count + " 个不同的业务（而不是同一份备份的轮换副本），"
            + "请改选「子目录单元」模板，每个子目录会各自成为一个业务单元"
        };

        var required = InferRequiredFiles(directories, evidence);
        var config = new JsonObject();
        AddRequired(config, required);

        return Build(
            source, "latest_directory", config,
            // 名字命中但时间分布勉强过线时给 62：低于 70 会连带把 NeedsDeeperScan 打开，
            // 让向导再抓一次看得更清楚，而不是拿一个勉强的结论往下走。
            confidence: clean ? 78 : 62,
            summary: $"{source.Name} 下的 {directories.Count} 个目录（{PreviewNames(directories)}）"
                     + $"是同一份备份的轮换副本，不是 {directories.Count} 个不同的业务："
                     + $"它们的最新文件时间依次错开约 {DescribeInterval(median)}，说明每次覆盖其中一个。"
                     + $"每次只认最新的那个（当前是 {current.Name}）"
                     + DescribeRequired(required),
            evidence, required, directories);
    }

    /// <summary>
    /// 整组名字是否落在同一个词表里且互不重复。判的是「整组」，不是「某个名字像星期几」——
    /// 单个名字撞上没有意义。命中返回词表名，否则 null。
    /// </summary>
    private static string? MatchRotationVocabulary(IReadOnlyList<string> names)
    {
        var distinct = names.Select(n => n.Trim().ToLowerInvariant()).ToList();
        if (distinct.Distinct().Count() != distinct.Count)
            return null;

        foreach (var (label, words) in RotationVocabularies)
        {
            var set = words.Select(w => w.ToLowerInvariant()).ToHashSet();
            if (distinct.All(set.Contains))
                return label;
        }

        // 纯序号：额外要求个数 ≥ 3 且最大值 ≤ 31。不加上限的话，
        // 1…18 这种账套编号会撞进来（时间判据也能挡，但在名字这一层先收一道更稳）。
        if (distinct.Count >= MinRotationSlots && distinct.All(n => n.Length is 1 or 2 && n.All(char.IsAsciiDigit)))
        {
            var values = distinct.Select(int.Parse).ToList();
            if (values.Max() <= 31 && values.Min() >= 0)
                return "纯序号";
        }

        return null;
    }

    /// <summary>该目录子树里最新文件的修改时间；一个文件都没有则为 null。</summary>
    private static DateTime? NewestFileTime(SnapshotNode node)
    {
        DateTime? newest = null;
        foreach (var file in node.EnumerateFiles(true))
        {
            if (file.LastModifiedAt is null)
                continue;
            if (newest is null || file.LastModifiedAt > newest)
                newest = file.LastModifiedAt;
        }

        return newest;
    }

    private static string DescribeInterval(double days) =>
        days >= 0.9
            ? $"{days:0.#} 天"
            : $"{days * 24:0.#} 小时";

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
                evidence, dated.Candidates, [source],
                dateNames: dated.GroupKeys);
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
        IReadOnlyList<SnapshotNode> NewestGroupFiles,
        /// <summary>所有分组的日期键，供 DateSequence 推周期与缺口。</summary>
        IReadOnlyList<string> GroupKeys);

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
            newest.Select(x => x.File).ToList(),
            groups.Select(g => g.Key).ToList());
    }

    private static List<string> DistinctExtensions(IEnumerable<SnapshotNode> files) =>
        files.Select(f => Extension(f.Name))
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>去掉扩展名的文件名。边界与 Extension 一致：前导点的文件不算有扩展名。</summary>
    /// <summary>
    /// 去掉扩展名之后的主干名。走 FileNamePattern.BaseName——`backup.tar.gz` 的主干是
    /// `backup` 而不是 `backup.tar`，否则 .tar.gz 与 .tar.bz2 会被分到两个「同名组」里。
    /// </summary>
    private static string BaseName(string name) => FileNamePattern.BaseName(name);

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
    ///   2. 模式必需    —— 归一化后的模式在 ≥90% 的单元里出现（UFFile_*.dat）
    ///   3. 附带文件    —— 其余，列出来但不勾选，并说明为什么
    ///
    /// 第 2 档曾经额外要求「各单元里的个数一致」，理由是 U8 的 UFFile 按年度分库、
    /// 各账套启用年度不同（2022 年起用的有 5 个，2024 年起用的只有 3 个），
    /// 拿个数当判据只会制造假警报。这条领域事实没错，但结论下过了头：
    /// **requiredFiles 的判据从来不是个数，是「这个模式至少匹配到一个」**
    /// （见 RecognizerRules.HasMissingRequired）。于是个数会波动的模式被整条排除在判据之外，
    /// 后果是「附件库一个都不剩」这件事没有任何一道检查拦得住——UFDATA.BAK 还在、
    /// 文件数下限还够、总大小还超（附件库比主库小得多），三道全过，静默入库。
    /// 所以第 2 档只看在场率，个数区间照常显示出来，让人知道这条判据管的是哪一件事。
    ///
    /// 分卷归档仍然单独一把尺子，见下面 volumeGaps 那一段。
    /// </summary>
    private static List<RequiredFileCandidateDto> InferRequiredFiles(
        IReadOnlyList<SnapshotNode> leaves, List<string> evidence)
    {
        if (leaves.Count == 0)
            return [];

        var leafFiles = leaves
            .Select(leaf => (Leaf: leaf, Files: VisibleFiles(leaf).ToList()))
            .Where(x => x.Files.Count > 0)
            .ToList();
        if (leafFiles.Count == 0)
            return [];

        var totalUnits = leafFiles.Count;
        var threshold = totalUnits == 1 ? 1 : (int)Math.Ceiling(totalUnits * RequiredThreshold);

        // ── 第一档：精确文件名 ──
        //
        // 分卷归档不走这一档：各单元卷数相同时 db.7z.001/.002/.003 三个精确名恰好都
        // 「每个单元都有」，于是它们会被逐个写进 requiredFiles——等于把**卷数写死**。
        // 数据量涨到四卷的那天，第四卷不在 requiredFiles 里没人管，
        // 而数据量缩到两卷时 requiredFiles 里的第三卷永远缺失、天天假警报。
        // 分卷该用的是第二档的主干模式 + 卷号连续性，见 FindVolumeGaps。
        var byName = CountAcross(leafFiles, f => f.Name, skip: f => FileNamePattern.ParseVolume(f.Name) is not null);
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
        var volumeGaps = FindVolumeGaps(leafFiles);
        var pattern = patterns
            .OrderByDescending(kv => kv.Value.PresentIn)
            .ThenByDescending(kv => kv.Value.Size)
            .Select(kv =>
            {
                var present = kv.Value.PresentIn >= threshold;

                // 分卷归档换一把尺子：判据是**卷号连续**，不是个数稳定。
                //
                // 「个数一致」那条对 U8 的 UFFile 附件库是对的（个数随账套启用年度天然不同），
                // 但对分卷用反了：分卷数随数据量浮动，于是 db_*.7z 永远个数不一致、
                // 永远不被推荐、缺一卷整份无法恢复却不告警。
                // 反过来，各单元卷数不同（3 卷 / 5 卷）但各自连续时**仍然推荐**——
                // 这正是与个数稳定性判据的区别。
                if (volumeGaps.TryGetValue(kv.Key, out var gap))
                {
                    return new RequiredFileCandidateDto
                    {
                        Pattern = kv.Key,
                        Kind = "pattern",
                        PresentIn = kv.Value.PresentIn,
                        TotalUnits = totalUnits,
                        TypicalSizeBytes = kv.Value.Size,
                        CountPerUnitMin = kv.Value.CountMin,
                        CountPerUnitMax = kv.Value.CountMax,
                        Recommended = present && gap.Length == 0,
                        NotRecommendedReason = !present
                            ? $"只出现在 {kv.Value.PresentIn}/{totalUnits} 个备份目录里"
                            : gap.Length == 0
                                ? null
                                : gap + "。分卷缺号意味着整份归档无法解压，请先确认备份脚本"
                    };
                }

                return new RequiredFileCandidateDto
                {
                    Pattern = kv.Key,
                    Kind = "pattern",
                    PresentIn = kv.Value.PresentIn,
                    TotalUnits = totalUnits,
                    TypicalSizeBytes = kv.Value.Size,
                    CountPerUnitMin = kv.Value.CountMin,
                    CountPerUnitMax = kv.Value.CountMax,
                    Recommended = present,
                    NotRecommendedReason = present
                        ? null
                        : $"只出现在 {kv.Value.PresentIn}/{totalUnits} 个备份目录里"
                };
            })
            .ToList();

        VolumePatterns = volumeGaps.Keys.ToList();

        var candidates = exact.Concat(pattern).Take(16).ToList();

        var recommended = candidates.Where(c => c.Recommended).Select(c => c.Pattern).ToList();
        if (recommended.Count > 0)
            evidence.Add($"考察的 {totalUnits} 个备份目录里，每一个都有：{string.Join("、", recommended)}");

        // 个数会浮动的模式，必须把判据本身说出来。不说的话，人看到「每个 2–5 个」
        // 会以为系统要求每次都有 5 个，然后自己把这条判据取消掉——而它恰恰是
        // 「附件库整批丢失」唯一的检出手段。
        foreach (var item in candidates.Where(c =>
                     c.Recommended && c.Kind == "pattern" && c.CountPerUnitMin != c.CountPerUnitMax))
        {
            evidence.Add(
                $"{item.Pattern}：各备份目录里的个数不一样（{item.CountPerUnitMin}–{item.CountPerUnitMax} 个），"
                + "这是正常的（U8 的附件库按年度分，各账套启用年度不同）。判据是「至少有一个」，"
                + "不是个数——整批丢失才会判为不完整");
        }

        foreach (var item in candidates.Where(c => !c.Recommended))
            evidence.Add($"{item.Pattern}：{item.NotRecommendedReason}，没有默认勾选");

        return candidates;
    }

    /// <summary>
    /// 上一次 InferRequiredFiles 认出来的分卷模式。调用方据此决定要不要往配置里写
    /// volumeContinuity——这个键要跟着 requiredFiles 一起落盘，扫描端才会做连续性检查。
    ///
    /// 用一个 [ThreadStatic] 的旁路而不是改 InferRequiredFiles 的返回类型：
    /// 那个方法有五个调用点，为一个布尔标志改五处签名不值当；
    /// 而 StructureInference 是无状态静态类、Infer 在一次请求里同步跑完，
    /// 线程内传一个标志是安全的。
    /// </summary>
    [ThreadStatic]
    private static List<string>? VolumePatterns;

    /// <summary>
    /// 哪些归一化模式是分卷归档，以及它们缺不缺卷。
    /// 值是空串表示各单元内部卷号都连续；非空是一句点名缺第几卷的人话。
    /// </summary>
    private static Dictionary<string, string> FindVolumeGaps(
        List<(SnapshotNode Leaf, List<SnapshotNode> Files)> leafFiles)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (leaf, files) in leafFiles)
        {
            // 一个单元内部，按「分卷主干」把卷号收拢
            var perStem = new Dictionary<string, (string Pattern, SortedSet<int> Indices)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var volume = FileNamePattern.ParseVolume(file.Name);
                if (volume is null)
                    continue;

                var patternKey = FileNamePattern.Normalize(file.Name);
                if (!perStem.TryGetValue(volume.Value.Stem, out var entry))
                    perStem[volume.Value.Stem] = entry = (patternKey, []);
                entry.Indices.Add(volume.Value.Index);
            }

            foreach (var (stem, entry) in perStem)
            {
                // 卷号必须从 1 连续到最大值。少于两卷不算分卷序列——
                // 单独一个 .001 更可能是别的命名习惯，按分卷判会把它错标成「缺卷」。
                if (entry.Indices.Count < 2)
                    continue;

                var gap = FirstGap(entry.Indices);
                var message = gap is null
                    ? string.Empty
                    : $"{leaf.Name} 缺第 {gap} 卷（现有 {string.Join("、", entry.Indices)}）";

                // 任一单元缺卷就整体判缺：这是一条完整性判据，一处不成立就不该推荐。
                if (!result.TryGetValue(entry.Pattern, out var existing) || existing.Length == 0)
                    result[entry.Pattern] = message;
            }
        }

        return result;
    }

    private static int? FirstGap(SortedSet<int> indices)
    {
        var expected = 1;
        foreach (var index in indices)
        {
            if (index != expected)
                return expected;
            expected++;
        }

        return null;
    }

    /// <summary>
    /// 按归一化模式统计：这个模式出现在多少个单元里，以及**每个单元里有几个**。
    /// 个数的最小/最大值是第二档能不能当判据的依据，见 InferRequiredFiles 的说明。
    /// </summary>
    private static Dictionary<string, (int PresentIn, int CountMin, int CountMax, long Size)> CountPatterns(
        List<(SnapshotNode Leaf, List<SnapshotNode> Files)> leafFiles, HashSet<string> alreadyCovered)
    {
        var result = new Dictionary<string, (int PresentIn, int CountMin, int CountMax, long Size)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (leaf, files) in leafFiles)
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

            // 抽样目录的修正：这个叶子里只有一种模式时，样本条数就等于该模式的真实个数——
            // 拿 Agent 回传的 FileCount 顶掉样本条数。不修正的话，一个抽了样的账套会显示
            // 「40 个」而没抽样的显示「57 个」，第二档的「个数一致」判据把本来一致的判成不一致，
            // 于是一条可靠的完整性判据被抽样这件事本身弄丢了。
            // 有多种模式时不修正：FileCount 是整个目录的总数，摊不到每个模式上，硬摊是编数据。
            if (leaf.Sampled && perUnit.Count == 1 && leaf.FileCount is > 0)
            {
                var only = perUnit.First();
                perUnit[only.Key] = (leaf.FileCount.Value, only.Value.Size);
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
        List<(SnapshotNode Leaf, List<SnapshotNode> Files)> leafFiles,
        Func<SnapshotNode, string> keySelector,
        Func<SnapshotNode, bool>? skip = null)
    {
        var result = new Dictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, files) in leafFiles)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (skip is not null && skip(file))
                    continue;
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

        // 推荐出来的模式里有分卷归档时，扫描端要额外做一次卷号连续性检查。
        // 不写这个键的话，requiredFiles 只保证「db_*.7z.* 这个模式有文件在场」，
        // 而缺第 2 卷时第 1、3、4 卷仍然在场——判据会说「齐了」，整份归档却解不开。
        var volumes = VolumePatterns;
        if (volumes is not null && recommended.Any(p => volumes.Contains(p, StringComparer.OrdinalIgnoreCase)))
            config["volumeContinuity"] = true;
    }

    private static string DescribeRequired(IReadOnlyList<RequiredFileCandidateDto> required)
    {
        var recommended = required.Where(c => c.Recommended).Select(c => c.Pattern).ToList();
        if (recommended.Count == 0)
            return "";
        // 说「固定有 N 个文件」会被读成「这个目录一共就 N 个文件」——而 U8 的一个日期目录里
        // 有 7 个文件，其中 2 个是固定名、5 个是按模式认的附件库。这句话的本意是
        // 「这几项每次都在」，就照这个意思写。
        return $"；每次都少不了这 {recommended.Count} 项：{string.Join("、", recommended)}";
    }

    /// <param name="dateNames">
    /// 这个结构里的日期名（日期目录名，或按日期分组的组键）。给出来才能推备份周期与历史缺口。
    /// </param>
    private static RecognizerProposalDto Build(
        SnapshotNode source,
        string recognizerType,
        JsonObject config,
        int confidence,
        string summary,
        List<string> evidence,
        IReadOnlyList<RequiredFileCandidateDto> required,
        IReadOnlyList<SnapshotNode> leaves,
        string? sourcePathOverride = null,
        IReadOnlyList<string>? dateNames = null)
    {
        // 快照本身没抓全时，推断依据就是不完整的，置信度必须相应下调并说明。
        //
        // Sampled 与 HasGaps 是两回事，刻意分开：抽样过的快照**每一层都有代表性样本**，
        // 结论是可信的，再按「不完整」扣分等于惩罚一个已经修好的问题。
        // 而 DepthLimited / Truncated / AccessDenied 意味着有整块结构没看见。
        if (source.HasGaps)
        {
            confidence = Math.Max(20, confidence - 20);
            evidence.Add("这个目录没有被完整抓取（层数或条目数触顶），以上结论只覆盖看得见的部分");
        }
        else if (source.HasSampling)
        {
            evidence.Add(
                "文件太多，每个目录只取了最新的一批做样本（目录本身、以及每个目录里的文件总数与总大小都是准确的）");
        }

        var application = GuessApplication(leaves);
        confidence = ApplyApplicationPrior(application, recognizerType, confidence, leaves, evidence);

        var baseline = SuggestSizeBaseline(leaves, required);
        var sequence = dateNames is null or { Count: 0 } ? null : DateSequence.Analyze(dateNames);
        if (sequence is not null)
        {
            summary += $"。这 {dateNames!.Count} 个日期跨了 {(sequence.Latest - sequence.Earliest).Days} 天，"
                       + $"是{sequence.PeriodLabel}一备";
            if (sequence.MissingDates.Count > 0)
            {
                evidence.Add(
                    $"按{sequence.PeriodLabel}一备推算，"
                    + string.Join("、", sequence.MissingDates.Take(10).Select(d => d.ToString("yyyy-MM-dd")))
                    + (sequence.MissingDates.Count > 10 ? $" 等 {sequence.MissingDates.Count} 天" : $" 这 {sequence.MissingDates.Count} 天")
                    + "没有对应的备份");
            }
        }

        if (baseline.Note is not null)
            evidence.Add(baseline.Note);

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
            SuggestedApplicationName = application,
            SuggestedMinTotalBytes = baseline.MinTotalBytes,
            SuggestedMinFileCount = baseline.MinFileCount,
            SizeBaselineNote = baseline.Note,
            // 周期与缺口只陈述事实：**不因为有缺口就降低置信度**。
            // 缺口是被识别对象的性质，不是识别质量的问题——把两者混在一起，
            // 会让「这个目录有历史缺备」表现成「向导看不懂这个目录」。
            DetectedPeriod = sequence?.PeriodLabel,
            MissingBackupDates = sequence?.MissingDates.Select(d => d.ToString("yyyy-MM-dd")).ToList() ?? []
        };
    }

    // ---------- B3：尺寸基线建议 ----------

    /// <summary>各单元大小相差超过这个倍数时不给建议值——统一阈值对它们没有意义。</summary>
    private const double SizeDispersionLimit = 20;

    /// <summary>建议值的下限。比 1 MB 还小的阈值检不出任何东西，只会让人以为设过了。</summary>
    private const long MinSuggestedBytes = 1024 * 1024;

    /// <summary>
    /// 从观察到的各份备份大小推一个下限建议值。
    ///
    /// 判据（ValidateSizeThresholds）本来就存在且告警链路完整，缺的只有这一环：
    /// 向导不给建议值，人要凭空想一个字节数填进去，于是现场基本不会生效——
    /// 而「备份文件突然变成 0 字节」是备份故障里最经典的一种。
    ///
    /// **只给下限，不给上限**：备份变大通常是业务正常增长，给上限会制造周期性假警报。
    /// 取最小一份的一半，是留出业务波动的余量——这个数要的是「掉了一个数量级」的检出力，
    /// 不是精确基线。
    /// </summary>
    private static (long? MinTotalBytes, int? MinFileCount, string? Note) SuggestSizeBaseline(
        IReadOnlyList<SnapshotNode> leaves,
        IReadOnlyList<RequiredFileCandidateDto> required)
    {
        var recommendedCount = required.Count(c => c.Recommended);
        var minFileCount = recommendedCount > 0 ? recommendedCount : (int?)null;

        var sizes = leaves.Select(TotalFileBytesOf).Where(b => b > 0).OrderBy(b => b).ToList();
        if (sizes.Count < 3)
        {
            return (null, minFileCount,
                $"只观察到 {sizes.Count} 份备份，样本太少，没有给出大小下限的建议值——"
                + "给了就是瞎猜。需要的话请在任务里手动设置。");
        }

        var min = sizes[0];
        var max = sizes[^1];

        // 离散度闸门。MinTotalBytes 是**任务级**的单一阈值，而 ValidateSizeThresholds
        // 拿它去比**每一个业务单元**的 TotalBytes。18 个账套里最小的 5 MB、最大的 1.5 GB 时，
        // 任何一个统一下限要么对大账套毫无检出力，要么对小账套天天假警报。
        // 宁可不给，也不要给一个假装有用的数。
        if (max > min * SizeDispersionLimit)
        {
            return (null, minFileCount,
                $"这 {sizes.Count} 份备份的大小相差 {max / Math.Max(1, min)} 倍"
                + $"（最小 {FormatBytes(min)}，最大 {FormatBytes(max)}），一个统一的下限对它们没有意义。"
                + "这一项留空，需要的话请对单个任务单独设置。");
        }

        var suggested = Math.Max(MinSuggestedBytes, min / 2 / MinSuggestedBytes * MinSuggestedBytes);
        return (suggested, minFileCount,
            $"观察到的 {sizes.Count} 份备份里最小的一份是 {FormatBytes(min)}，"
            + $"建议下限设为 {FormatBytes(suggested)}。低于这个值会判「备份大小不对」并告警。");
    }

    /// <summary>
    /// 一份备份的总字节数。优先用 Agent 回传的 TotalFileBytes（含抽样未返回的部分），
    /// 没有就按快照里看得见的文件加总——抽样时后者会偏小，而这个数是用来定告警阈值的，
    /// 偏小意味着阈值偏低、漏报，比偏高误报安全。
    /// </summary>
    private static long TotalFileBytesOf(SnapshotNode leaf) =>
        leaf.TotalFileBytes ?? VisibleFiles(leaf).Sum(f => f.SizeBytes);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }

    // ---------- B8.1：应用先验反哺判据 ----------

    /// <summary>
    /// 认出应用之后，用该应用的已知形态**叠加**一点置信度并补一句 Evidence。
    ///
    /// 三条硬约束，缺一条这个加成就会变成「用猜测取消该做的检查」：
    ///   1. 只在通用判据**已经成立**时叠加（recognizerType 不是兜底的那两种），
    ///      否则一个恰好有 .bak 的目录会被强行套上 SQL Server 的结构假设；
    ///   2. 加成上限 +8；
    ///   3. **不得把一个低于 70 的结论顶到 70 以上**——那会把 NeedsDeeperScan 关掉。
    /// </summary>
    private static int ApplyApplicationPrior(
        string? application,
        string recognizerType,
        int confidence,
        IReadOnlyList<SnapshotNode> leaves,
        List<string> evidence)
    {
        if (application is null || leaves.Count == 0)
            return confidence;

        var names = leaves.SelectMany(VisibleFiles).Select(f => f.Name.ToLowerInvariant()).ToList();

        if (application == "SQL Server" && names.Any(n => n.EndsWith(".trn")) && names.Any(n => n.EndsWith(".bak")))
        {
            evidence.Add(
                "这里有完整备份（.bak）和日志备份（.trn）两类文件，"
                + "请确认这个任务是不是应该只认 .bak——两类都收的话，每次备份的文件清单会随日志备份的频率变动");
        }

        if (application != "用友 U8" || recognizerType != "subdirectory_units")
            return confidence;

        // 低于 70 的结论一律不加成：那条线决定要不要再抓一次看清楚，
        // 而先验回答不了「有没有看全」这个问题。
        if (confidence < 70)
            return confidence;

        evidence.Add("文件特征符合用友 U8 的备份形态（账套目录 + UFDATA.BAK），与推断出的结构一致");
        return Math.Min(100, confidence + 8);
    }

    /// <summary>从文件特征猜应用名。猜不出就返回 null，让人自己填——瞎猜一个名字没有价值。</summary>

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
    /// 深度上限由调用方给（来自本次快照实际抓了几层，见 Infer 的 snapshotMaxDepth）。
    /// 这个数不能只留在这里：它必须随配置一起写成 unitMaxDepth，预演和真扫才会用同一个
    /// 下探层数（见调用处写 config 的那段）。
    /// </summary>
    private static List<UnitCandidate> ResolveUnits(SnapshotNode source, int unitMaxDepth)
    {
        var rules = RecognizerRules.Empty() with { UnitLayout = "date_leaf" };
        return BusinessUnitResolver
            .Resolve(source, rules, SnapshotRecognizer.SnapshotNavigator(source), maxDepth: unitMaxDepth)
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

    /// <summary>
    /// 文件扩展名。走 FileNamePattern.Extension，复合扩展名（.tar.gz / .sql.gz）整体识别——
    /// 只取最后一个点会让成对检测与扩展名分组同时串味，而那正是 R1 最危险缺陷的所在区域。
    /// </summary>
    private static string Extension(string name) => FileNamePattern.Extension(name);

    private static string PreviewNames(IReadOnlyList<SnapshotNode> nodes)
    {
        var names = nodes.Take(3).Select(n => n.Name).ToList();
        return nodes.Count > 3
            ? string.Join("、", names) + $" … 共 {nodes.Count} 个"
            : string.Join("、", names);
    }
}
