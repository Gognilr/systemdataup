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
public sealed record RecognizerRules(
    List<string> Includes,
    List<string> Excludes,
    List<string> Required,
    List<string> ExcludeDirectories,
    bool Recursive,
    string? BatchRegex,
    int BusinessUnitDepth = 1,
    string? UnitLayout = null,
    string? GroupBy = null)
{
    public static RecognizerRules Empty() => new([], [], [], [], true, null, 1, null, null);

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
        "recursive", "businessUnitDepth", "unitLayout", "batchRegex", "groupBy"
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

            if (root.TryGetProperty("recursive", out var recursive)
                && (recursive.ValueKind == JsonValueKind.True || recursive.ValueKind == JsonValueKind.False))
            {
                result = result with { Recursive = recursive.GetBoolean() };
            }

            if (root.TryGetProperty("businessUnitDepth", out var depth)
                && depth.TryGetInt32(out var parsedDepth)
                && parsedDepth > 0)
            {
                result = result with { BusinessUnitDepth = Math.Min(parsedDepth, 32) };
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
    /// <summary>某个文件是否被 requiredFiles 点名。</summary>
    public bool IsRequiredFile(string relativePath) =>
        Required.Count == 0 || Required.Any(pattern => MatchesPath(relativePath, pattern));

    /// <summary>规则要求的文件是否有缺失。</summary>
    public bool HasMissingRequired(IEnumerable<string> relativePaths)
    {
        if (Required.Count == 0)
            return false;
        var paths = relativePaths.ToList();
        return Required.Any(pattern => !paths.Any(path => MatchesPath(path, pattern)));
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
