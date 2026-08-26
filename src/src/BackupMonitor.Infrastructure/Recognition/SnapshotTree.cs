using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Infrastructure.Recognition;

/// <summary>
/// 目录快照的内存形态。
///
/// Agent 回来的 BrowseSnapshotDto 是一棵按"相对根路径"组织的树；推断和预演都需要
/// 按绝对路径定位、按层级遍历、判断某一段是否被截断，所以先折成这个节点模型。
/// </summary>
public sealed class SnapshotNode
{
    public string Name { get; init; } = string.Empty;

    /// <summary>绝对路径，统一用 '/' 分隔以便比较。</summary>
    public string FullPath { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime? LastModifiedAt { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>该目录到达抓取深度上限，下面的内容这次没取。</summary>
    public bool DepthLimited { get; init; }

    /// <summary>该目录的子项因限额未能全部返回。</summary>
    public bool Truncated { get; init; }

    public bool AccessDenied { get; init; }

    public List<SnapshotNode> Children { get; init; } = [];

    public IEnumerable<SnapshotNode> Directories => Children.Where(c => c.IsDirectory);
    public IEnumerable<SnapshotNode> Files => Children.Where(c => !c.IsDirectory);

    /// <summary>这棵子树里是否存在没抓全的地方——预演结论要据此声明自己不完整。</summary>
    public bool HasGaps =>
        DepthLimited || Truncated || AccessDenied || Children.Any(c => c.HasGaps);

    /// <summary>按绝对路径在子树里查找节点，大小写不敏感（Windows 路径）。</summary>
    public SnapshotNode? Resolve(string absolutePath)
    {
        var target = Normalize(absolutePath);
        if (string.Equals(Normalize(FullPath), target, StringComparison.OrdinalIgnoreCase))
            return this;

        foreach (var child in Children)
        {
            var found = child.Resolve(absolutePath);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>枚举子树中位于指定相对层级的目录（1 = 直接子目录）。</summary>
    public IEnumerable<SnapshotNode> DirectoriesAtDepth(int depth)
    {
        if (depth <= 0)
            return [];
        if (depth == 1)
            return Directories;
        return Directories.SelectMany(d => d.DirectoriesAtDepth(depth - 1));
    }

    /// <summary>子树里的所有文件（recursive=false 时只取直接子文件）。</summary>
    public IEnumerable<SnapshotNode> EnumerateFiles(bool recursive)
    {
        foreach (var file in Files)
            yield return file;

        if (!recursive)
            yield break;

        foreach (var directory in Directories)
        {
            foreach (var file in directory.EnumerateFiles(true))
                yield return file;
        }
    }

    public static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

    /// <summary>快照 DTO → 根节点。根节点代表 snapshot.Root 这个目录本身。</summary>
    public static SnapshotNode FromSnapshot(BrowseSnapshotDto snapshot)
    {
        var root = Normalize(snapshot.Root);
        var node = new SnapshotNode
        {
            Name = LeafName(root),
            FullPath = root,
            IsDirectory = true,
            Truncated = snapshot.Truncated,
            Children = snapshot.Entries.Select(e => FromEntry(root, e)).ToList()
        };
        return node;
    }

    private static SnapshotNode FromEntry(string root, BrowseEntryDto entry)
    {
        var relative = Normalize(entry.RelativePath);
        var full = string.IsNullOrEmpty(root)
            ? relative
            : $"{root}/{relative}";

        return new SnapshotNode
        {
            Name = string.IsNullOrWhiteSpace(entry.Name) ? LeafName(relative) : entry.Name,
            FullPath = Normalize(full),
            IsDirectory = entry.IsDirectory,
            SizeBytes = entry.SizeBytes ?? 0,
            LastModifiedAt = entry.LastModifiedAt,
            IsHidden = entry.IsHidden,
            DepthLimited = entry.DepthLimited,
            Truncated = entry.Truncated,
            AccessDenied = entry.AccessDenied,
            Children = entry.Children.Select(c => FromEntry(root, c)).ToList()
        };
    }

    private static string LeafName(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }
}
