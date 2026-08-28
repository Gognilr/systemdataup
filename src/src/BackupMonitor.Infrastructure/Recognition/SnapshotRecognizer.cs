using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Recognition;

namespace BackupMonitor.Infrastructure.Recognition;

/// <summary>
/// 在目录快照上预演一条识别规则。
///
/// 存在的意义是把反馈从"十几秒"压到"毫秒"：识别测试要下发指令、等 Agent 领走、
/// 真扫一遍磁盘；预演跑在服务端已有的快照上。管理员改一个勾选就能立刻看到判定变化，
/// 配置这件事才从"填完再说"变成"看着调"。
///
/// 遍历方式必须与 Agent 的 BackupScanner 一一对应，规则语义则共用 RecognizerRules——
/// 后者是硬约束：向导说会识别出什么，实际扫描就必须识别出什么。
/// </summary>
public static class SnapshotRecognizer
{
    public static RecognizerPreviewDto Preview(
        SnapshotNode source,
        string recognizerType,
        RecognizerRules rules,
        int stabilityIntervalSeconds,
        DateTime capturedAt,
        IReadOnlyList<string> deniedPaths,
        bool snapshotTruncated)
    {
        var preview = new RecognizerPreviewDto
        {
            SourcePath = source.FullPath,
            RecognizerType = recognizerType,
            SnapshotTruncated = snapshotTruncated,
            SnapshotDepthLimited = source.HasGaps,
            DeniedPaths = deniedPaths.ToList(),
            CapturedAt = capturedAt
        };

        foreach (var (root, unitName) in SelectRoots(source, recognizerType, rules))
        {
            var unit = BuildUnit(source, root, unitName, recognizerType, rules, stabilityIntervalSeconds, capturedAt);
            preview.Units.Add(unit);
        }

        preview.PassedCount = preview.Units.Count(u => u.Status == "passed");
        preview.FailedCount = preview.Units.Count - preview.PassedCount;
        return preview;
    }

    private static RecognizerPreviewUnitDto BuildUnit(
        SnapshotNode source,
        SnapshotNode root,
        string? unitName,
        string recognizerType,
        RecognizerRules rules,
        int stabilityIntervalSeconds,
        DateTime capturedAt)
    {
        var unit = new RecognizerPreviewUnitDto
        {
            BusinessUnit = unitName,
            SourceRoot = RelativeTo(source.FullPath, root.FullPath),
            Incomplete = root.HasGaps
        };

        // 与 BackupScanner.ScanAsync 对称的一步，共用 RecognizerRules 里那份收敛实现。
        // 位置同样在 files.Count == 0 判断之前：收敛后为空要走 no_new_backup。
        var files = rules.NarrowToOneBackup(
            recognizerType,
            CollectFiles(root, rules).ToList(),
            f => f.Relative,
            f => f.Node.LastModifiedAt ?? DateTime.MinValue).ToList();
        if (files.Count == 0)
        {
            unit.Status = "no_new_backup";
            unit.FailureMessage = root.HasGaps
                ? "未发现符合识别规则的备份文件（该目录未抓取完整，结论可能不准）"
                : "未发现符合识别规则的备份文件";
            return unit;
        }

        var newest = files.Max(f => f.Node.LastModifiedAt ?? DateTime.MinValue);
        unit.NewestFileAt = newest == DateTime.MinValue ? null : newest;
        unit.TotalFiles = files.Count;
        unit.TotalBytes = files.Sum(f => f.Node.SizeBytes);

        // 稳定观察窗口按快照采集时刻回放，而不是按"现在"。
        // 快照是某一时刻的观察结果，用当前时间去比会把当时明明还在写的文件算成已稳定。
        if (stabilityIntervalSeconds > 0
            && newest != DateTime.MinValue
            && capturedAt - newest < TimeSpan.FromSeconds(stabilityIntervalSeconds))
        {
            unit.Status = "still_changing";
            unit.FailureMessage = "最新文件仍处于稳定观察窗口";
        }
        else
        {
            var missing = rules.Required
                .Where(pattern => !files.Any(f => RecognizerRules.MatchesPath(f.Relative, pattern)))
                .ToList();
            if (missing.Count > 0)
            {
                unit.Status = "required_file_missing";
                unit.FailureMessage = "识别规则要求的文件未全部出现";
                unit.MissingRequired = missing;
            }
            else
            {
                unit.Status = "passed";
            }
        }

        unit.Files = files
            .OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .Select(f => new RecognizerPreviewFileDto
            {
                RelativePath = f.Relative,
                SizeBytes = f.Node.SizeBytes,
                LastModifiedAt = f.Node.LastModifiedAt ?? DateTime.MinValue,
                IsRequired = rules.IsRequiredFile(f.Relative)
            })
            .ToList();

        return unit;
    }

    /// <summary>
    /// 与 BackupScanner.ScanAsync 开头那段通配展开一一对应的快照版本，
    /// 共用 WildcardPath 里那一份逐段展开实现。
    ///
    /// 不共用的话，向导预演的是一个目录、Agent 实际扫描的是另一个——
    /// 而这种分叉恰恰在"跨月自动跟随"这类场景下最不容易被人发现。
    /// </summary>
    /// <returns>展开后的节点；失败时 Source 为 null 且 FailureMessage 有值。</returns>
    public static (SnapshotNode? Source, string? FailureMessage) ExpandSource(
        SnapshotNode root, string? sourcePathPattern, RecognizerRules rules)
    {
        if (string.IsNullOrWhiteSpace(sourcePathPattern))
            return (root, null);
        if (!WildcardPath.ContainsWildcard(sourcePathPattern))
            return (root.Resolve(sourcePathPattern.Trim()), null);

        var expansion = WildcardPath.Expand(
            sourcePathPattern.Trim(),
            current => root.Resolve(current)?.Directories.Where(d => !rules.IsExcludedDirectory(d.FullPath))
                       ?? Enumerable.Empty<SnapshotNode>(),
            directory => directory.Name,
            directory => directory.FullPath,
            directory => NewestAllowedFile(directory, rules));

        if (!expansion.Success)
            return (null, expansion.FailureMessage);

        var node = root.Resolve(expansion.Path!);
        return node is null
            ? (null, $"通配展开到 {expansion.Path}，但它不在本次目录浏览抓到的范围内，无法预演。")
            : (node, null);
    }

    /// <summary>与 BackupScanner.SelectRoots 一一对应的快照版本。</summary>
    public static List<(SnapshotNode Root, string? UnitName)> SelectRoots(
        SnapshotNode source, string recognizerType, RecognizerRules rules)
    {
        var normalized = (recognizerType ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized == "subdirectory_units")
        {
            // 与 BackupScanner 共用 BusinessUnitResolver：单元怎么找只能有一份定义，
            // 否则向导预演出 18 个账套、真扫出 3 个，而这种分叉没有任何人会发现。
            var units = BusinessUnitResolver.Resolve(source, rules, SnapshotNavigator(source));
            var pickLatestChild = rules.UnitLayout is "latest_directory" or "date_leaf";

            return units
                .Select(u => (
                    Root: pickLatestChild ? SelectLatestChildDirectory(u.Directory, rules) : u.Directory,
                    UnitName: (string?)u.RelativePath))
                .ToList();
        }

        if (normalized == "latest_directory")
        {
            var directories = EnumerateDirectories(source, rules.Recursive)
                .Where(d => !rules.IsExcludedDirectory(d.FullPath))
                .Where(d => rules.BatchRegex is null
                            || RecognizerRules.RegexMatches(RelativeTo(source.FullPath, d.FullPath), rules.BatchRegex)
                            || RecognizerRules.RegexMatches(d.Name, rules.BatchRegex))
                .ToList();

            var latest = directories
                .Select(d => new { Node = d, Latest = NewestAllowedFile(d, rules) })
                .OrderByDescending(x => x.Latest)
                .FirstOrDefault();

            return latest is null ? [(source, null)] : [(latest.Node, null)];
        }

        // 整个源目录算一份备份——multi_file_set 的定义，不是兜底。理由见 BackupScanner 同名分支。
        if (normalized == "multi_file_set")
            return [(source, null)];

        if (normalized == "latest_single_file")
        {
            var latest = CollectFiles(source, rules, forceRecursive: rules.Recursive)
                .OrderByDescending(f => f.Node.LastModifiedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            if (latest is null)
                return [(source, null)];

            var parent = FindParentDirectory(source, latest.Node) ?? source;
            return [(parent, null)];
        }

        return [(source, null)];
    }

    /// <summary>把目录快照折成 BusinessUnitResolver 需要的四个访问器（与 BackupScanner 那份一一对应）。</summary>
    public static BusinessUnitResolver.Navigator<SnapshotNode> SnapshotNavigator(SnapshotNode source) => new()
    {
        ChildDirectories = node => node.Directories,
        Name = node => node.Name,
        FullPath = node => node.FullPath,
        RelativePath = node => RelativeTo(source.FullPath, node.FullPath)
    };

    private static SnapshotNode SelectLatestChildDirectory(SnapshotNode unitRoot, RecognizerRules rules)
    {
        var latest = unitRoot.Directories
            .Where(d => !rules.IsExcludedDirectory(d.FullPath))
            .Select(d => new { Node = d, Latest = NewestAllowedFile(d, rules) })
            .OrderByDescending(x => x.Latest)
            .ThenByDescending(x => x.Node.FullPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return latest?.Node ?? unitRoot;
    }

    private static DateTime NewestAllowedFile(SnapshotNode directory, RecognizerRules rules) =>
        CollectFiles(directory, rules, forceRecursive: true)
            .Select(f => f.Node.LastModifiedAt ?? DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

    /// <summary>按规则收集某个采集根下的文件。</summary>
    public static IEnumerable<CollectedFile> CollectFiles(SnapshotNode root, RecognizerRules rules, bool? forceRecursive = null)
    {
        var recursive = forceRecursive ?? rules.Recursive;
        var rootName = root.Name;
        foreach (var file in root.EnumerateFiles(recursive))
        {
            var relative = RelativeTo(root.FullPath, file.FullPath);
            if (rules.IsExcludedDirectory(ParentPath(file.FullPath)))
                continue;
            if (!rules.IsAllowedFile(relative, file.FullPath, file.Name, file.IsHidden, rootName))
                continue;
            yield return new CollectedFile(file, relative);
        }
    }

    private static IEnumerable<SnapshotNode> EnumerateDirectories(SnapshotNode root, bool recursive)
    {
        foreach (var directory in root.Directories)
        {
            yield return directory;
            if (!recursive)
                continue;
            foreach (var nested in EnumerateDirectories(directory, true))
                yield return nested;
        }
    }

    private static SnapshotNode? FindParentDirectory(SnapshotNode root, SnapshotNode file)
    {
        if (root.Children.Contains(file))
            return root;
        foreach (var directory in root.Directories)
        {
            var found = FindParentDirectory(directory, file);
            if (found is not null)
                return found;
        }

        return null;
    }

    public static string RelativeTo(string root, string path)
    {
        var normalizedRoot = SnapshotNode.Normalize(root);
        var normalizedPath = SnapshotNode.Normalize(path);
        if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return ".";
        if (normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
            return normalizedPath[(normalizedRoot.Length + 1)..];
        return normalizedPath;
    }

    private static string ParentPath(string path)
    {
        var normalized = SnapshotNode.Normalize(path);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[..index];
    }

    public sealed record CollectedFile(SnapshotNode Node, string Relative);
}
