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
            var unit = BuildUnit(source, root, unitName, rules, stabilityIntervalSeconds, capturedAt);
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

        var files = CollectFiles(root, rules).ToList();
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

    /// <summary>与 BackupScanner.SelectRoots 一一对应的快照版本。</summary>
    public static List<(SnapshotNode Root, string? UnitName)> SelectRoots(
        SnapshotNode source, string recognizerType, RecognizerRules rules)
    {
        var normalized = (recognizerType ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized == "subdirectory_units")
        {
            var units = source.DirectoriesAtDepth(rules.BusinessUnitDepth)
                .Where(d => !rules.IsExcludedDirectory(d.FullPath))
                .Where(d => rules.BatchRegex is null || RecognizerRules.RegexMatches(d.Name, rules.BatchRegex))
                .OrderBy(d => d.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(d => (Node: d, Name: RelativeTo(source.FullPath, d.FullPath)))
                .ToList();

            if (rules.UnitLayout == "latest_directory")
            {
                return units
                    .Select(u => (Root: SelectLatestChildDirectory(u.Node, rules), UnitName: (string?)u.Name))
                    .ToList();
            }

            return units.Select(u => (u.Node, (string?)u.Name)).ToList();
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
