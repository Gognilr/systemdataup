using BackupMonitor.Shared.Security;

namespace BackupMonitor.Shared.Recognition;

/// <summary>
/// 源路径里的 <c>*</c> 展开。
///
/// 存在的理由：源目录写死成 D:\Seeyon\A6\Backup\2026\08，到了 9 月、到了 2027 年任务
/// 就失效了，而失效的表现是 path_not_found —— 需要有人记得去改。写成
/// D:\Seeyon\A6\Backup\*\* 之后跨月、跨年自动跟随。
///
/// 展开语义（写在这里，是为了避免日后被改成"取全部"）：
/// **逐段展开，每一段的多个候选中只取最新的一个**，最终得到唯一的实际目录。
/// 一个任务对应一个采集根；取全部会让"一份备份"重新失去边界。
///
/// "最新"的判定沿用 SelectLatestChildDirectory 已有的口径——按目录内最新的、符合规则的
/// 文件时间，而不是目录自身的时间戳：很多备份程序先建目录再慢慢往里写，目录时间
/// 没有参考价值。候选目录里一个文件都没有时（例如被清空的 07），其时间是
/// DateTime.MinValue，自然排在后面。
///
/// 与 RecognizerRules 同理，实现只有这一份：Agent 展开真实文件系统，服务端展开快照。
/// 分叉的表现是"向导预演的是一个目录、实际扫描的是另一个"，且无人察觉。
/// </summary>
public static class WildcardPath
{
    public const char Wildcard = '*';

    /// <summary>展开结果。<see cref="Path"/> 为 null 时 <see cref="FailureMessage"/> 一定有值。</summary>
    public readonly record struct Expansion(string? Path, string? FailureMessage)
    {
        public bool Success => Path is not null;

        public static Expansion Ok(string path) => new(path, null);
        public static Expansion Fail(string message) => new(null, message);
    }

    public static bool ContainsWildcard(string? path) =>
        !string.IsNullOrEmpty(path) && path.IndexOf(Wildcard) >= 0;

    /// <summary>
    /// 通配路径的字面前缀：第一个含 <c>*</c> 的段之前的部分。
    /// 展开结果必须仍落在它之下，这是防穿越的围栏。
    /// </summary>
    public static string LiteralPrefixOf(string pattern)
    {
        var normalized = Normalize(pattern);
        var root = System.IO.Path.GetPathRoot(normalized) ?? string.Empty;
        var current = root;
        foreach (var segment in SegmentsOf(normalized, root))
        {
            if (segment.IndexOf(Wildcard) >= 0)
                break;
            current = current.Length == 0 ? segment : System.IO.Path.Combine(current, segment);
        }

        return current;
    }

    /// <summary>
    /// 逐段展开通配路径。<typeparamref name="TDirectory"/> 在 Agent 侧是真实目录，
    /// 在服务端侧是快照节点——两侧共用这一份遍历。
    /// </summary>
    /// <param name="pattern">可能含 * 的源路径</param>
    /// <param name="childDirectoriesOf">给定目录路径，返回其直接子目录</param>
    /// <param name="nameOf">取候选目录的名字，用来与通配段比对</param>
    /// <param name="pathOf">取候选目录的完整路径</param>
    /// <param name="newestContentTimeOf">取候选目录内最新的、符合规则的文件时间</param>
    public static Expansion Expand<TDirectory>(
        string pattern,
        Func<string, IEnumerable<TDirectory>> childDirectoriesOf,
        Func<TDirectory, string> nameOf,
        Func<TDirectory, string> pathOf,
        Func<TDirectory, DateTime> newestContentTimeOf)
    {
        var normalized = Normalize(pattern);
        var root = System.IO.Path.GetPathRoot(normalized) ?? string.Empty;

        // 通配只允许出现在目录段。盘符段带 * 意味着"在所有磁盘上找"，
        // 那是完全不同的一件事，不在本功能范围内，必须明确拒绝而不是猜。
        //
        // 两道检查是必需的：GetPathRoot 认得 \\*\share 这类（返回带 * 的根），
        // 但对 *:\ 这种非法盘符在 .NET Core 上返回空字符串，于是 "*:" 会掉进
        // 普通目录段里，报出一条毫不相干的"没有匹配到任何目录"。
        if (root.IndexOf(Wildcard) >= 0)
            return Expansion.Fail($"源路径的通配不能出现在盘符段：{pattern}");

        var segments = SegmentsOf(normalized, root);
        if (root.Length == 0
            && segments.Count > 0
            && segments[0].Contains(':')
            && segments[0].IndexOf(Wildcard) >= 0)
        {
            return Expansion.Fail($"源路径的通配不能出现在盘符段：{pattern}");
        }

        if (segments.Count == 0)
            return Expansion.Fail($"源路径无法解析：{pattern}");

        // 展开本身只会把 * 替换成枚举到的真实目录名，永远不会引入 ..；
        // 唯一的穿越来源是人写进来的 ..，所以在这里一次性挡掉。
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                return Expansion.Fail($"源路径不能包含 . 或 .. 路径段：{pattern}");
        }

        var current = root;
        foreach (var segment in segments)
        {
            if (segment.IndexOf(Wildcard) < 0)
            {
                current = current.Length == 0 ? segment : System.IO.Path.Combine(current, segment);
                continue;
            }

            var candidates = childDirectoriesOf(current)
                .Where(d => RecognizerRules.GlobMatch(nameOf(d), segment))
                .ToList();
            if (candidates.Count == 0)
            {
                return Expansion.Fail(
                    $"源路径的通配没有匹配到任何目录：{current} 下没有名字符合 {segment} 的子目录"
                    + $"（完整通配：{pattern}）。这不是路径写错了，是当前没有可匹配的目录。");
            }

            // 时间相同则按名字倒序，保证结果稳定而不依赖枚举顺序。
            var newest = candidates
                .OrderByDescending(newestContentTimeOf)
                .ThenByDescending(nameOf, StringComparer.OrdinalIgnoreCase)
                .First();
            current = pathOf(newest);
        }

        // 围栏：展开结果必须仍在字面前缀之下。复用既有的 PathSafety.IsUnderBase，
        // 不另写一套包含判定。
        var prefix = LiteralPrefixOf(pattern);
        if (!PathSafety.IsUnderBase(prefix, current))
        {
            return Expansion.Fail(
                $"源路径的通配展开结果 {current} 落到了 {prefix} 之外，已拒绝（完整通配：{pattern}）。");
        }

        return Expansion.Ok(current);
    }

    private static string Normalize(string pattern) =>
        pattern.Trim().Replace('/', System.IO.Path.DirectorySeparatorChar);

    private static List<string> SegmentsOf(string normalized, string root) =>
        normalized[root.Length..]
            .Split(
                [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .ToList();
}
