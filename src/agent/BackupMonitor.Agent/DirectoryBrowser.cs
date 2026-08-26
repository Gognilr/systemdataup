using System.Diagnostics;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>
/// 目录快照采集：为建任务向导提供"客户端上有什么"的只读视图。
///
/// 三条不可动摇的边界：
/// 1. 只返回元数据。文件内容永不经过这条通道——否则这就不是目录浏览器，
///    而是一个装在管理界面里的远程读文件工具。
/// 2. 有限额。备份盘下面动辄几十万个文件，无限制枚举会把 Agent 和网络一起拖垮，
///    所以条目数、深度、耗时三个维度都有硬上限，触顶就截断并如实标记。
/// 3. 拒绝访问要说出来。备份目录恰恰是最容易设 ACL 的地方，
///    "这里是空的"和"这里我看不见"对使用者是完全不同的两件事。
/// </summary>
public sealed class DirectoryBrowser
{
    private readonly AgentOptions _options;
    private readonly ILogger<DirectoryBrowser> _logger;

    public DirectoryBrowser(IOptions<AgentOptions> options, ILogger<DirectoryBrowser> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public BrowseSnapshotDto Browse(BrowsePathCommandPayload payload, CancellationToken ct)
    {
        var maxDepth = Math.Clamp(payload.MaxDepth <= 0 ? 3 : payload.MaxDepth, 1, Math.Max(1, _options.BrowseMaxDepth));
        var maxEntries = Math.Clamp(payload.MaxEntries <= 0 ? 3000 : payload.MaxEntries, 10, Math.Max(10, _options.BrowseMaxEntries));
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(payload.Path))
            return BrowseDrives(stopwatch);

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(payload.Path.Trim()));
        if (!IsWithinBrowseRoots(root))
            throw new UnauthorizedAccessException($"路径不在允许浏览的范围内：{root}");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"目录不存在：{root}");

        var state = new BrowseState(maxEntries, stopwatch, TimeSpan.FromSeconds(Math.Max(5, _options.BrowseTimeoutSeconds)));
        var entries = Enumerate(root, root, 1, maxDepth, state, ct);

        stopwatch.Stop();
        return new BrowseSnapshotDto
        {
            Root = root,
            IsDriveList = false,
            Entries = entries,
            TotalEntries = state.Count,
            Truncated = state.Truncated,
            MaxDepth = maxDepth,
            CapturedAt = DateTime.UtcNow,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            DeniedPaths = state.Denied
        };
    }

    /// <summary>
    /// 起点：本机固定磁盘。给的是"打开我的电脑"那一屏，而不是让人先凭空想出一个路径。
    /// </summary>
    private BrowseSnapshotDto BrowseDrives(Stopwatch stopwatch)
    {
        var entries = new List<BrowseEntryDto>();
        foreach (var drive in SafeGetDrives())
        {
            var rootPath = drive.RootDirectory.FullName;
            if (!IsWithinBrowseRoots(rootPath))
                continue;

            entries.Add(new BrowseEntryDto
            {
                Name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? rootPath.TrimEnd('\\')
                    : $"{rootPath.TrimEnd('\\')} {drive.VolumeLabel}",
                RelativePath = rootPath,
                IsDirectory = true,
                DepthLimited = true,
                ChildCount = null,
                SizeBytes = SafeAvailableFreeSpace(drive),
                LastModifiedAt = null
            });
        }

        // 白名单模式下磁盘可能一个都不剩，此时直接把白名单本身列出来当起点。
        if (entries.Count == 0 && _options.BrowseRoots.Length > 0)
        {
            foreach (var allowed in _options.BrowseRoots)
            {
                var expanded = SafeFullPath(allowed);
                if (expanded is null || !Directory.Exists(expanded))
                    continue;
                entries.Add(new BrowseEntryDto
                {
                    Name = expanded,
                    RelativePath = expanded,
                    IsDirectory = true,
                    DepthLimited = true
                });
            }
        }

        stopwatch.Stop();
        return new BrowseSnapshotDto
        {
            Root = string.Empty,
            IsDriveList = true,
            Entries = entries,
            TotalEntries = entries.Count,
            Truncated = false,
            MaxDepth = 0,
            CapturedAt = DateTime.UtcNow,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private List<BrowseEntryDto> Enumerate(
        string root, string current, int depth, int maxDepth, BrowseState state, CancellationToken ct)
    {
        var result = new List<BrowseEntryDto>();
        if (state.Exhausted)
            return result;

        List<string> directories;
        List<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(current).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            files = Directory.EnumerateFiles(current).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            state.Denied.Add(current);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "枚举目录失败 path={Path}", current);
            state.Denied.Add(current);
            return result;
        }

        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            if (!state.Take())
                break;

            var entry = BuildDirectoryEntry(root, directory);
            if (depth < maxDepth && !entry.AccessDenied)
            {
                entry.Children = Enumerate(root, directory, depth + 1, maxDepth, state, ct);
                entry.Truncated = entry.ChildCount is > 0 && entry.Children.Count < entry.ChildCount;
            }
            else if (!entry.AccessDenied)
            {
                entry.DepthLimited = entry.ChildCount is null or > 0;
            }

            result.Add(entry);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!state.Take())
                break;

            result.Add(BuildFileEntry(root, file));
        }

        return result;
    }

    private BrowseEntryDto BuildDirectoryEntry(string root, string path)
    {
        var entry = new BrowseEntryDto
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            RelativePath = Relative(root, path),
            IsDirectory = true
        };

        try
        {
            var info = new DirectoryInfo(path);
            entry.LastModifiedAt = info.LastWriteTimeUtc;
            entry.IsHidden = (info.Attributes & FileAttributes.Hidden) != 0;
        }
        catch (Exception)
        {
            // 属性读不到不影响它作为一个目录被列出来。
        }

        try
        {
            entry.ChildCount = Directory.EnumerateFileSystemEntries(path).Count();
        }
        catch (UnauthorizedAccessException)
        {
            entry.AccessDenied = true;
        }
        catch (Exception)
        {
            entry.AccessDenied = true;
        }

        return entry;
    }

    private static BrowseEntryDto BuildFileEntry(string root, string path)
    {
        var entry = new BrowseEntryDto
        {
            Name = Path.GetFileName(path),
            RelativePath = Relative(root, path),
            IsDirectory = false
        };

        try
        {
            var info = new FileInfo(path);
            entry.SizeBytes = info.Length;
            entry.LastModifiedAt = info.LastWriteTimeUtc;
            entry.IsHidden = (info.Attributes & FileAttributes.Hidden) != 0;
        }
        catch (Exception)
        {
            // 同上：读不到属性的文件仍然要出现在清单里。
        }

        return entry;
    }

    private static string Relative(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
        catch (Exception)
        {
            return path.Replace('\\', '/');
        }
    }

    /// <summary>
    /// 白名单校验。留空 = 全部固定磁盘。
    /// 用 GetFullPath 归一化后再比，防止 ..\..\ 绕出白名单。
    /// </summary>
    private bool IsWithinBrowseRoots(string fullPath)
    {
        if (_options.BrowseRoots.Length == 0)
            return true;

        foreach (var allowed in _options.BrowseRoots)
        {
            var expanded = SafeFullPath(allowed);
            if (expanded is null)
                continue;

            if (string.Equals(fullPath, expanded, StringComparison.OrdinalIgnoreCase))
                return true;

            var prefix = expanded.EndsWith(Path.DirectorySeparatorChar)
                ? expanded
                : expanded + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<DriveInfo> SafeGetDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            return [];
        }

        return drives.Where(d =>
        {
            try
            {
                return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Network;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }

    private static long? SafeAvailableFreeSpace(DriveInfo drive)
    {
        try { return drive.AvailableFreeSpace; }
        catch (Exception) { return null; }
    }

    /// <summary>条目数与耗时的共用限额。任一维度触顶，整次枚举就停在那里并标记截断。</summary>
    private sealed class BrowseState
    {
        private readonly int _maxEntries;
        private readonly Stopwatch _stopwatch;
        private readonly TimeSpan _timeout;

        public BrowseState(int maxEntries, Stopwatch stopwatch, TimeSpan timeout)
        {
            _maxEntries = maxEntries;
            _stopwatch = stopwatch;
            _timeout = timeout;
        }

        public int Count { get; private set; }
        public bool Truncated { get; private set; }
        public List<string> Denied { get; } = [];

        public bool Exhausted => Truncated;

        public bool Take()
        {
            if (Truncated)
                return false;
            if (Count >= _maxEntries || _stopwatch.Elapsed > _timeout)
            {
                Truncated = true;
                return false;
            }

            Count++;
            return true;
        }
    }
}
