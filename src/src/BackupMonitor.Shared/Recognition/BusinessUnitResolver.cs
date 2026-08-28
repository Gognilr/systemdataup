namespace BackupMonitor.Shared.Recognition;

/// <summary>
/// 「源目录下面，哪些目录算一个业务单元」。
///
/// 这里存在的理由是一个表达不出来的结构：
///
/// <code>
/// D:\自动备份\
///   ├── ZT001\20260822\…          ← 单元在第 1 层
///   ├── ZT002\20260828\…          ← 单元在第 1 层
///   └── ZT201-ZT216\              ← 纯容器，本身不是单元
///        ├── ZT201\20260828\…     ← 单元在第 2 层
///        └── … ZT216\20260828\…
/// </code>
///
/// businessUnitDepth 是一个整数，语义是「全树统一深度」，任何取值都覆盖不了它：
/// 取 1，ZT201-ZT216 被当成一个单元，扫描时会把 ZT216 底下 7 天的文件混成一份备份，
/// **判 passed 不告警**，而 ZT201–ZT215 这 15 个账套压根不在监控范围内；
/// 取 2，ZT001 的日期目录反倒成了单元。
///
/// 所以这里给出第三种找法：按**结构**找，而不是按深度找。
///
/// 三种模式的优先级是「越具体越优先」：
/// businessUnitPaths（人指的）→ date_leaf（按结构自适应）→ businessUnitDepth（按深度）。
/// 能说死的就说死，说不死才用自适应——自适应会猜错，而写死的深度不会。
///
/// 泛型是因为它要在两种「目录」上跑：Agent 扫真实文件系统（DirectoryInfo），
/// 服务端在目录快照上预演（SnapshotNode）。两侧各写一份的话，向导说会识别出
/// 18 个账套、实际扫描出 3 个，而且没有任何人会发现。
/// </summary>
public static class BusinessUnitResolver
{
    /// <summary>date_leaf 模式下探的层数上限。够深到能容纳「分组目录 → 账套 → 日期」再多一层。</summary>
    public const int DefaultMaxDepth = 6;

    /// <summary>一个业务单元：目录本身、它相对源目录的路径（就是 external_key）、以及它在第几层。</summary>
    public readonly record struct Unit<TDir>(TDir Directory, string RelativePath, int Depth);

    /// <summary>目录树的访问方式。两个实现（真实文件系统 / 目录快照）各提供一份。</summary>
    public sealed class Navigator<TDir>
    {
        public required Func<TDir, IEnumerable<TDir>> ChildDirectories { get; init; }
        public required Func<TDir, string> Name { get; init; }
        public required Func<TDir, string> FullPath { get; init; }

        /// <summary>相对源目录的路径，'/' 分隔。</summary>
        public required Func<TDir, string> RelativePath { get; init; }
    }

    public static List<Unit<TDir>> Resolve<TDir>(
        TDir source,
        RecognizerRules rules,
        Navigator<TDir> navigator,
        int maxDepth = DefaultMaxDepth)
    {
        var units = rules switch
        {
            { BusinessUnitPaths.Count: > 0 } => ByPaths(source, rules, navigator, maxDepth),
            { UnitLayout: "date_leaf" } => ByDateLeaf(source, rules, navigator, maxDepth),
            _ => ByDepth(source, rules, navigator)
        };

        return units
            .Where(u => rules.BatchRegex is null
                        || RecognizerRules.RegexMatches(navigator.Name(u.Directory), rules.BatchRegex))
            .OrderBy(u => u.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>人自己指定的单元位置（相对源目录的通配，如 `ZT0*`、`ZT201-ZT216/*`）。</summary>
    private static List<Unit<TDir>> ByPaths<TDir>(
        TDir source, RecognizerRules rules, Navigator<TDir> navigator, int maxDepth)
    {
        var result = new List<Unit<TDir>>();
        Walk(source, 1);
        return result;

        void Walk(TDir directory, int depth)
        {
            if (depth > maxDepth)
                return;

            foreach (var child in Allowed(directory, rules, navigator))
            {
                var relative = navigator.RelativePath(child);
                if (rules.BusinessUnitPaths.Any(pattern => RecognizerRules.GlobMatch(relative, pattern)))
                {
                    result.Add(new Unit<TDir>(child, relative, depth));
                    continue; // 命中即止：单元里面不会再有单元。
                }

                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// 按结构找：**直接子目录多数是日期目录的那一层，就是业务单元**。
    ///
    /// 找不到日期层时把当前这一枝的目录本身当成单元，而不是让它凭空消失。
    /// 这一条不是兜底写法，是刻意的：一个刚建出来还没备份过的账套、或者今天没跑出备份的账套，
    /// 应当照常出现在结果里并判「未发现符合识别规则的备份文件」——那正是要告警的情况。
    /// 让它从单元列表里消失，等于「没备份」表现为「没这个账套」，是最坏的一种静默。
    /// </summary>
    private static List<Unit<TDir>> ByDateLeaf<TDir>(
        TDir source, RecognizerRules rules, Navigator<TDir> navigator, int maxDepth)
    {
        var result = new List<Unit<TDir>>();
        foreach (var child in Allowed(source, rules, navigator))
            result.AddRange(Descend(child, 1));
        return result;

        List<Unit<TDir>> Descend(TDir directory, int depth)
        {
            var self = new List<Unit<TDir>>
            {
                new(directory, navigator.RelativePath(directory), depth)
            };

            if (depth >= maxDepth)
                return self;

            var children = Allowed(directory, rules, navigator).ToList();
            if (children.Count == 0)
                return self;

            var names = children.Select(navigator.Name).ToList();
            if (DateName.IsDateLayer(names))
                return self;

            var nested = children.SelectMany(c => Descend(c, depth + 1)).ToList();
            return nested.Count > 0 ? nested : self;
        }
    }

    /// <summary>按固定深度找。历史行为，businessUnitDepth 没被 date_leaf / businessUnitPaths 顶掉时走这条。</summary>
    private static List<Unit<TDir>> ByDepth<TDir>(
        TDir source, RecognizerRules rules, Navigator<TDir> navigator)
    {
        var depth = Math.Max(1, rules.BusinessUnitDepth);
        var current = new List<TDir> { source };
        for (var level = 1; level <= depth; level++)
            current = current.SelectMany(d => Allowed(d, rules, navigator)).ToList();

        return current
            .Select(d => new Unit<TDir>(d, navigator.RelativePath(d), depth))
            .ToList();
    }

    private static IEnumerable<TDir> Allowed<TDir>(
        TDir directory, RecognizerRules rules, Navigator<TDir> navigator) =>
        navigator.ChildDirectories(directory)
            .Where(d => !rules.IsExcludedDirectory(navigator.FullPath(d)));
}
