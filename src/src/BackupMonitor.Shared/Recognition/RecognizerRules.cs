using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackupMonitor.Shared.Recognition;

/// <summary>
/// 识别规则的解析与匹配语义。
///
/// 这段逻辑原本只存在于 Agent 的 BackupScanner 里。配置向导需要在服务端「预演」
/// 一条规则在目录快照上的判定结果——如果服务端另写一份匹配实现，两边迟早会分叉，
/// 而分叉的表现是最糟的那种：向导告诉管理员「这条规则会识别出这些文件」，
/// 真正扫描时却是另一个结果，且没有任何人会发现。
///
/// 因此规则语义只在这里实现一次：Agent 扫真实文件系统，服务端扫快照，
/// 两者共用同一套 Parse / GlobMatch / IsAllowedFile。
/// </summary>
/// <param name="UnitMaxDepth">
/// 业务单元下探的层数上限，**由向导推断时把它实际用过的那个数写进配置**。
///
/// 存在的理由：这个数原本没有落盘，于是三个调用点各取各的——向导推断显式传 4、
/// 向导预演和 Agent 真扫都取 BusinessUnitResolver.DefaultMaxDepth（6）。
/// 快照只有 4 层时预演无处可去，碰巧与推断一致；Agent 面对的却是真实文件系统，
/// date_leaf 会一路下探到第 6 层。目录比快照深、且置信度 ≥ 70（NeedsDeeperScan 为 false，
/// 向导不会再抓更深的快照）时，向导展示的单元集合与 Agent 扫出来的不是同一批，
/// external_key 取的是相对路径，也跟着变——而这种分叉没有任何提示。
///
/// 所以「用了几层」必须和规则一起存下来：三方读同一个数，而不是各自取默认值。
/// 为 null 表示配置里没写（存量任务、手写配置），此时才回落到 DefaultMaxDepth。
/// </param>
public sealed record RecognizerRules(
    List<string> Includes,
    List<string> Excludes,
    List<string> Required,
    List<string> ExcludeDirectories,
    bool Recursive,
    string? BatchRegex,
    int BusinessUnitDepth = 1,
    string? UnitLayout = null,
    string? GroupBy = null,
    List<string>? BusinessUnitPathPatterns = null,
    int? UnitMaxDepth = null,
    bool VolumeContinuity = false)
{
    /// <summary>
    /// 人自己指定的业务单元位置（相对源目录的通配，如 `ZT0*`、`ZT201-ZT216/*`）。
    /// 非空时优先于 unitLayout 与 businessUnitDepth——他指的地方一定比系统猜的准。
    /// </summary>
    public List<string> BusinessUnitPaths { get; } = BusinessUnitPathPatterns ?? [];

    public static RecognizerRules Empty() => new([], [], [], [], true, null, 1, null, null, [], null);

    /// <summary>
    /// 识别规则里所有被承认的键名。
    ///
    /// 存在的理由：ParseRules 对不认识的键一律静默忽略。把 requiredFiles 敲成
    /// requireFiles，任务保存成功、界面一切正常、规则完全失效——直到某天真要恢复
    /// 数据时才发现。校验端拿这份清单可以当场指出拼错的键。
    /// </summary>
    public static readonly IReadOnlyList<string> KnownKeys =
    [
        "includePatterns", "includes",
        "excludePatterns", "excludes",
        "excludeDirectories", "excludeDirs",
        "requiredFiles", "requiredPatterns", "required",
        "recursive", "businessUnitDepth", "unitLayout", "batchRegex", "groupBy",
        "businessUnitPaths", "unitMaxDepth", "volumeContinuity"
    ];

    /// <summary>
    /// 返回 JSON 顶层出现的、不在 KnownKeys 里的键名。
    /// JSON 非法或不是对象时返回空——那由别的校验负责报错，这里不越权。
    /// </summary>
    public static IReadOnlyList<string> FindUnknownKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            return document.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => !KnownKeys.Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// 为一个未知键找出最像的已知键（编辑距离 ≤ 3 且不超过键长的一半）。
    /// 找不到就返回 null——宁可不提示，也不要把人往错的方向指。
    /// </summary>
    public static string? SuggestKey(string unknownKey)
    {
        if (string.IsNullOrWhiteSpace(unknownKey))
            return null;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var known in KnownKeys)
        {
            var distance = EditDistance(unknownKey.ToLowerInvariant(), known.ToLowerInvariant());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = known;
            }
        }

        var tolerance = Math.Min(3, Math.Max(1, unknownKey.Length / 2));
        return bestDistance <= tolerance ? best : null;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>识别规则 JSON → 规则对象。非法 JSON 退回默认规则（由配置校验阶段负责报错）。</summary>
    public static RecognizerRules Parse(string? json)
    {
        var result = Empty();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return result;

            result.Includes.AddRange(ReadStrings(root, "includePatterns"));
            result.Includes.AddRange(ReadStrings(root, "includes"));
            result.Excludes.AddRange(ReadStrings(root, "excludePatterns"));
            result.Excludes.AddRange(ReadStrings(root, "excludes"));
            result.ExcludeDirectories.AddRange(ReadStrings(root, "excludeDirectories"));
            result.ExcludeDirectories.AddRange(ReadStrings(root, "excludeDirs"));
            result.Required.AddRange(ReadStrings(root, "requiredFiles"));
            result.Required.AddRange(ReadStrings(root, "requiredPatterns"));
            result.Required.AddRange(ReadStrings(root, "required"));
            result.BusinessUnitPaths.AddRange(ReadStrings(root, "businessUnitPaths"));

            if (root.TryGetProperty("recursive", out var recursive)
                && (recursive.ValueKind == JsonValueKind.True || recursive.ValueKind == JsonValueKind.False))
            {
                result = result with { Recursive = recursive.GetBoolean() };
            }

            if (root.TryGetProperty("volumeContinuity", out var volumeContinuity)
                && (volumeContinuity.ValueKind == JsonValueKind.True || volumeContinuity.ValueKind == JsonValueKind.False))
            {
                result = result with { VolumeContinuity = volumeContinuity.GetBoolean() };
            }

            if (root.TryGetProperty("businessUnitDepth", out var depth)
                && depth.TryGetInt32(out var parsedDepth)
                && parsedDepth > 0)
            {
                result = result with { BusinessUnitDepth = Math.Min(parsedDepth, 32) };
            }

            // 上限同样收到 32：这个值最终变成 date_leaf 的递归深度，
            // 一条手写成 unitMaxDepth: 1000000 的配置不该把 Agent 拖进一次深不见底的遍历。
            if (root.TryGetProperty("unitMaxDepth", out var unitMaxDepth)
                && unitMaxDepth.TryGetInt32(out var parsedUnitMaxDepth)
                && parsedUnitMaxDepth > 0)
            {
                result = result with { UnitMaxDepth = Math.Min(parsedUnitMaxDepth, 32) };
            }

            if (root.TryGetProperty("unitLayout", out var unitLayout)
                && unitLayout.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(unitLayout.GetString()))
            {
                result = result with { UnitLayout = unitLayout.GetString()!.Trim().ToLowerInvariant() };
            }

            if (root.TryGetProperty("groupBy", out var groupBy)
                && groupBy.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(groupBy.GetString()))
            {
                result = result with { GroupBy = groupBy.GetString()!.Trim() };
            }

            if (root.TryGetProperty("batchRegex", out var batchRegex)
                && batchRegex.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(batchRegex.GetString()))
            {
                result = result with { BatchRegex = batchRegex.GetString() };
            }
        }
        catch (JsonException)
        {
            // 无效的识别规则仍允许基础文件发现，服务端会在配置校验阶段提示管理员。
        }

        return result;
    }

    private static IEnumerable<string> ReadStrings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!];
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
            : [];
    }

    /// <summary>
    /// 一个文件是否落入规则。语义与 Agent 端扫描完全一致：
    /// 显式排除优先 → batchRegex → includePatterns → 被点名的文件免受启发式判断 → 半成品启发式。
    /// </summary>
    /// <param name="relativePath">相对采集根的路径，'/' 分隔</param>
    /// <param name="fullPath">完整路径（excludePatterns/includePatterns 允许写绝对路径通配）</param>
    /// <param name="fileName">文件名</param>
    /// <param name="isHidden">是否隐藏文件</param>
    /// <param name="rootName">采集根的目录名（batchRegex 允许匹配它）</param>
    public bool IsAllowedFile(string relativePath, string fullPath, string fileName, bool isHidden, string rootName)
    {
        var relative = relativePath.Replace('\\', '/');

        // 显式排除永远优先。
        if (Excludes.Any(pattern => MatchesPath(relative, pattern) || GlobMatch(fullPath, pattern)))
            return false;

        if (BatchRegex is not null
            && !RegexMatches(relative, BatchRegex)
            && !RegexMatches(rootName, BatchRegex))
            return false;

        var matchesInclude = Includes.Any(pattern => MatchesPath(relative, pattern) || GlobMatch(fullPath, pattern));
        if (Includes.Count > 0 && !matchesInclude)
            return false;

        // 被 includePatterns 或 requiredFiles 点名的文件跳过下面的启发式判断：
        // 管理员已经明确说了要这个文件，系统没有资格替他判断那是不是半成品。
        if (matchesInclude || Required.Any(pattern => MatchesPath(relative, pattern)))
            return true;

        // 启发式：正在写入或明显是半成品的文件不该被当成一份备份。
        //
        // 注意 .bak 不在此列。它曾经在，但那是把通用桌面经验（config.ini.bak 是编辑器留下的副本）
        // 搬进了备份领域——SQL Server 的 BACKUP DATABASE 默认产出的就是 .bak，
        // 在一个备份监控系统里把 .bak 当垃圾滤掉，等于对最常见的备份格式集体失明，
        // 而且失明得毫无提示：界面上只会显示「未发现符合识别规则的备份文件」。
        // 真要排除随手留下的 .bak，用 excludePatterns 明确写出来。
        if (isHidden)
            return false;

        return !fileName.StartsWith('~')
               && !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               && !fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从收集到的文件里挑出「最新的一组」。
    ///
    /// 为什么在这里而不在两侧各写一份：与本文件头部注释的理由完全相同。
    /// 分组语义一旦 Agent 与服务端分叉，表现是「向导说会识别出这些文件、实际扫描是另一批」，
    /// 而且没有人会察觉。
    ///
    /// 为什么是「收集之后的一次筛选」而不是一种新识别器：SelectRoots 的返回值是目录，
    /// 而文件组不是目录；要让它返回文件组就得改签名，波及两处 SelectRoots 的全部分支。
    /// 插在 CollectFiles 之后只需要一个切入点，且任何识别器类型都能叠加使用。
    ///
    /// 规则：按分组键归组 → 每组取组内最新文件时间作为该组时间 → 取时间最大的一组；
    /// 时间相同则按分组键倒序，保证结果稳定而不依赖枚举顺序（R1 的教训）。
    /// GroupBy 为空时原样返回——这是默认值，所以存量任务行为完全不变。
    /// </summary>
    /// <param name="files">已经过 IsAllowedFile 筛选的文件</param>
    /// <param name="relativePathOf">取文件相对采集根的路径，'/' 或 '\' 分隔均可</param>
    /// <param name="lastWriteOf">取文件最后修改时间</param>
    public IReadOnlyList<T> SelectLatestGroup<T>(
        IReadOnlyList<T> files,
        Func<T, string> relativePathOf,
        Func<T, DateTime> lastWriteOf)
    {
        if (string.IsNullOrWhiteSpace(GroupBy) || files.Count == 0)
            return files;

        var groups = files
            .GroupBy(file => GroupKeyOf(relativePathOf(file)), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count <= 1)
            return files;

        var newest = groups
            .OrderByDescending(g => g.Max(lastWriteOf))
            .ThenByDescending(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .First();

        return newest.ToList();
    }

    /// <summary>
    /// 收集完文件之后，把它们收敛成「一份备份」的唯一入口。
    ///
    /// 现在有两件事挂在这个位置：latest_single_file 只保留最新的那一个文件，
    /// groupBy 只保留最新的那一组。两侧（Agent 真扫 / 服务端预演）都必须调它，
    /// 否则向导算出来的一份备份和实际传上去的一份备份不是同一批文件。
    ///
    /// latest_single_file 为什么要在这里补一刀：这个识别器此前**名不副实**。
    /// SelectRoots 只用「最新」挑出那个文件所在的**目录**，随后照常收集整个目录，
    /// 于是「取最新的那个文件」实际行为是「取那个目录里的全部文件」。
    /// 在一个堆了 7 天备份的目录上，后果是每次扫描把 14 个文件全部读一遍算 SHA-256、
    /// 每次上传 10.59GB 而不是 1.5GB，而且 manifest 每天都变。
    /// </summary>
    public IReadOnlyList<T> NarrowToOneBackup<T>(
        string? recognizerType,
        IReadOnlyList<T> files,
        Func<T, string> relativePathOf,
        Func<T, DateTime> lastWriteOf)
    {
        if (string.Equals(recognizerType?.Trim(), "latest_single_file", StringComparison.OrdinalIgnoreCase)
            && files.Count > 1)
        {
            // 时间相同时按路径倒序，保证结果稳定而不依赖枚举顺序——与 SelectLatestGroup 同一条教训。
            var newest = files
                .OrderByDescending(lastWriteOf)
                .ThenByDescending(relativePathOf, StringComparer.OrdinalIgnoreCase)
                .First();
            return [newest];
        }

        return SelectLatestGroup(files, relativePathOf, lastWriteOf);
    }

    /// <summary>
    /// 一个文件的分组键。
    ///
    /// - "basename"：去掉扩展名的文件名（致远 OA 的 NAME.zip + NAME.properties）；
    /// - 其它字符串：当作正则，取第一个捕获组；
    ///   编译不过或匹配不上的文件归入 <see cref="UngroupedKey"/> 并保留——
    ///   沿用 RegexMatches 已有的「异常吞掉、退化为不过滤」策略。一条写错的 groupBy
    ///   不该让备份凭空消失。
    /// </summary>
    public string GroupKeyOf(string relativePath)
    {
        var fileName = FileNameOf(relativePath.Replace('\\', '/'));
        if (string.Equals(GroupBy, "basename", StringComparison.OrdinalIgnoreCase))
        {
            var index = fileName.LastIndexOf('.');
            return index <= 0 ? fileName : fileName[..index];
        }

        try
        {
            var match = Regex.Match(
                fileName,
                GroupBy!,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (match.Success && match.Groups.Count > 1 && match.Groups[1].Success)
                return match.Groups[1].Value;
        }
        catch (ArgumentException)
        {
            // 正则编译不过：所有文件归入同一个"未分组"键，等价于不分组。
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return UngroupedKey;
    }

    /// <summary>无法从 groupBy 得出分组键的文件的归属。刻意用一个文件名里不可能出现的值。</summary>
    public const string UngroupedKey = "\u0000ungrouped";

    /// <summary>
    /// 某个文件是否被 requiredFiles 点名。
    ///
    /// 没配 requiredFiles 时返回 false，而不是 true。这一行原先是
    /// `Required.Count == 0 || …`，于是「一条必需文件都没配」被当成「每个文件都必需」——
    /// 向导界面上一边写着「这次没有看出固定出现的文件，暂不检查」，一边给下面
    /// 14 个文件全挂上「必需」角标，同一个事实在同一屏里自相矛盾。
    ///
    /// 判定逻辑不受影响：HasMissingRequired 在 Required 为空时本来就返回 false，
    /// 这个标志只用于 CandidateFile.IsRequired 这个展示字段。
    /// </summary>
    public bool IsRequiredFile(string relativePath) =>
        Required.Count > 0 && Required.Any(pattern => MatchesPath(relativePath, pattern));

    /// <summary>规则要求的文件是否有缺失。</summary>
    public bool HasMissingRequired(IEnumerable<string> relativePaths)
    {
        if (Required.Count == 0)
            return false;
        var paths = relativePaths.ToList();
        return Required.Any(pattern => !paths.Any(path => MatchesPath(path, pattern)));
    }

    /// <summary>
    /// 分卷连续性检查：每一组分卷的卷号必须从 1 连续到最大值，中间不缺号。
    /// 缺号返回一句人话（点名缺第几卷），完整返回 null。
    ///
    /// volumeContinuity 为 false 时永远返回 null——这是一条**选择开启**的判据，
    /// 不是对所有任务的普遍要求：多数备份根本不是分卷形态。
    ///
    /// 与 HasMissingRequired 分开而不是塞进去：那一条问的是「该有的文件在不在」，
    /// 这一条问的是「在的这些文件构不构成一份完整归档」。
    /// 缺一卷时整份归档无法解压，但每一卷本身都在场——两条判据回答的不是同一个问题。
    /// </summary>
    public string? FindVolumeGap(IEnumerable<string> relativePaths)
    {
        if (!VolumeContinuity)
            return null;

        var groups = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        var tails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in relativePaths)
        {
            var name = FileNameOf(path);
            var volume = FileNamePattern.ParseVolume(name);
            if (volume is not null)
            {
                if (!groups.TryGetValue(volume.Value.Stem, out var set))
                    groups[volume.Value.Stem] = set = [];
                set.Add(volume.Value.Index);
                continue;
            }

            var tail = FileNamePattern.VolumeTailStem(name);
            if (tail is not null)
                tails.Add(tail);
        }

        foreach (var (stem, indices) in groups)
        {
            // .z01/.z02 这种形态的末卷是 .zip 本身，它没有卷号。末卷不在场时整份同样解不开，
            // 所以它缺席要单独说——而不是把它算成「卷号不连续」，那会指错方向。
            if (stem.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || stem.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                if (!tails.Contains(stem))
                    return $"分卷归档 {stem} 缺末卷 {stem}（只有 {string.Join("、", indices)} 号分卷）";
            }

            var expected = 1;
            foreach (var index in indices)
            {
                if (index != expected)
                    return $"分卷归档 {stem} 缺第 {expected} 卷（现有 {string.Join("、", indices)}）";
                expected++;
            }
        }

        return null;
    }

    /// <summary>路径的任一段命中 excludeDirectories 即为被排除目录。</summary>
    public bool IsExcludedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || ExcludeDirectories.Count == 0)
            return false;

        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => ExcludeDirectories.Any(excluded => GlobMatch(segment, excluded.Trim())));
    }

    /// <summary>通配匹配相对路径，或只匹配文件名——所以 requiredFiles 里可以直接写 UFDATA.BAK。</summary>
    public static bool MatchesPath(string relative, string pattern) =>
        GlobMatch(relative, pattern) || GlobMatch(FileNameOf(relative), pattern);

    public static bool GlobMatch(string value, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value.Replace('\\', '/'), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool RegexMatches(string value, string pattern)
    {
        try
        {
            return Regex.IsMatch(
                value,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string FileNameOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }
}
