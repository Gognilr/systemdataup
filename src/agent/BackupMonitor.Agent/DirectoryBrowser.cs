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
        var rootStats = new DirectoryStats();
        var entries = Enumerate(root, maxDepth, state, rootStats, ct);

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
            DeniedPaths = state.Denied,
            RootFileCount = rootStats.FileCount,
            RootTotalFileBytes = rootStats.TotalBytes,
            RootSampled = rootStats.Sampled
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

    /// <summary>单个目录里最多带回多少个文件的明细。超出的部分只记总数与总字节。</summary>
    private const int MaxFilesPerDirectory = 40;

    /// <summary>每目录配额的下限。算出来比这还小时不再往下压——那等于哪个目录都看不清。</summary>
    private const int MinPerDirectoryQuota = 20;

    /// <summary>某个目录的文件统计：总数、总字节、是否做过抽样。</summary>
    private sealed class DirectoryStats
    {
        public int FileCount;
        public long TotalBytes;
        public bool Sampled;
    }

    /// <summary>广度优先推进时的待展开项：这个目录，以及它的子项该挂到哪里。</summary>
    private sealed record Pending(string Path, BrowseEntryDto? Entry, List<BrowseEntryDto> Sink);

    /// <summary>
    /// 逐层广度优先枚举，同一层内每个目录一份配额。
    ///
    /// 为什么不是深度优先：结构推断的每一条判据都是**按层算的比例**
    /// （第一层是不是日期层、单元层里带日期子目录的占比、叶子层的必需文件占比），
    /// 而深度优先在条目预算触顶时丢掉的永远是字典序靠后的那一批——
    /// 「用字典序截断的样本去算比例」这两件事单独看都合理，凑在一起就不成立了。
    /// 举一个会真错的例子：ZT001 下挂了个历史归档吃掉大半预算，剩下 17 个账套只抓到前几个，
    /// unitDateRatio 用 3–4 个样本算出来，任何一个不规整的都能把比例压到阈值以下，
    /// 本来看得懂的结构变成「没能看懂」；反过来靠前的几个恰好规整，就以 1.0 的比例给出高置信度。
    ///
    /// BFS 保证每一层都看得见；同层内的每目录配额保证一个胖目录吃不掉整层。
    /// 叶子里的文件再做抽样（只带最新的 N 个明细，但回传真实的总数与总字节），
    /// 因为推断需要的是「每个叶子有几个文件、是什么模式」，不是每个文件的名字。
    /// </summary>
    private List<BrowseEntryDto> Enumerate(
        string root, int maxDepth, BrowseState state, DirectoryStats rootStats, CancellationToken ct)
    {
        var rootEntries = new List<BrowseEntryDto>();
        var frontier = new List<Pending> { new(root, null, rootEntries) };

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0 && !state.Exhausted; depth++)
        {
            // 配额按「本层还剩多少预算 / 本层有多少个目录」分。除不动时兜到下限，
            // 宁可让这一层提前触顶，也不要给每个目录发一个连一条都装不下的额度。
            var quota = Math.Max(MinPerDirectoryQuota, state.Remaining / Math.Max(1, frontier.Count));
            var next = new List<Pending>();

            foreach (var pending in frontier)
            {
                ct.ThrowIfCancellationRequested();
                if (state.Exhausted)
                    break;

                ExpandOne(root, pending, depth, maxDepth, quota, state, rootStats, next, ct);
            }

            frontier = next;
        }

        // 还没展开就到深度上限（或预算耗尽）的目录：如实标记，不能表现成「它是空的」。
        foreach (var pending in frontier)
        {
            if (pending.Entry is { AccessDenied: false, IsReparsePoint: false })
                pending.Entry.DepthLimited = pending.Entry.ChildCount is null or > 0;
        }

        return rootEntries;
    }

    private void ExpandOne(
        string root, Pending pending, int depth, int maxDepth, int quota,
        BrowseState state, DirectoryStats rootStats, List<Pending> next, CancellationToken ct)
    {
        List<string> directories;
        List<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(pending.Path)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            files = Directory.EnumerateFiles(pending.Path).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            state.Denied.Add(pending.Path);
            if (pending.Entry is not null)
                pending.Entry.AccessDenied = true;
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "枚举目录失败 path={Path}", pending.Path);
            state.Denied.Add(pending.Path);
            if (pending.Entry is not null)
                pending.Entry.AccessDenied = true;
            return;
        }

        // 文件统计先于抽样：回传的总数与总字节永远是真实值，
        // 抽掉的只是明细。InferRequiredFiles 的「各单元里个数是否一致」那一档靠它才判得准。
        var stats = pending.Entry is null ? rootStats : new DirectoryStats();
        var infos = new List<FileInfo>(files.Count);
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                stats.FileCount++;
                stats.TotalBytes += info.Length;
                infos.Add(info);
            }
            catch (Exception)
            {
                // 读不到属性的文件仍然要计入个数，只是没有大小可加。
                stats.FileCount++;
            }
        }

        var budget = quota;
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            if (budget <= 0 || !state.Take())
            {
                MarkTruncated(pending.Entry);
                return;
            }

            budget--;
            var entry = BuildDirectoryEntry(root, directory);
            pending.Sink.Add(entry);

            // reparse point 不下探：备份盘上做 junction 指向另一个卷很常见，
            // 跟进去会把同一批文件重复计入统计、白白吃掉条目预算。
            // 但要照常列出并标记——静默跳过会让「这里有东西」表现成「这里是空的」。
            if (depth < maxDepth && !entry.AccessDenied && !entry.IsReparsePoint)
                next.Add(new Pending(directory, entry, entry.Children));
            else if (!entry.AccessDenied && !entry.IsReparsePoint)
                entry.DepthLimited = entry.ChildCount is null or > 0;
        }

        // 抽样只在预算真的紧张时才发生：装得下就全带回来，行为与从前逐字节一致。
        // 装不下才退到「最新的 N 个」——取 mtime 最新而不是字典序前 N 个，
        // 因为识别关心的是最近一份备份长什么样。
        //
        // 这个「装得下就不抽」很重要：60 个文件的目录在 3000 条预算下根本不吃紧，
        // 无条件砍到 40 会把「30 组成对文件」变成「20 组」——一个本来完全看得清的结构，
        // 被一条为大目录准备的措施弄成了残缺样本。
        var sampledFiles = infos.Count <= budget
            ? infos
            : infos.OrderByDescending(SafeWriteTime).Take(Math.Min(budget, MaxFilesPerDirectory)).ToList();
        stats.Sampled = infos.Count > sampledFiles.Count;

        if (pending.Entry is not null)
        {
            pending.Entry.FileCount = stats.FileCount;
            pending.Entry.TotalFileBytes = stats.TotalBytes;
            pending.Entry.Sampled = stats.Sampled;
        }

        foreach (var file in sampledFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (budget <= 0 || !state.Take())
            {
                MarkTruncated(pending.Entry);
                return;
            }

            budget--;
            pending.Sink.Add(BuildFileEntry(root, file));
        }
    }

    private static void MarkTruncated(BrowseEntryDto? entry)
    {
        if (entry is not null)
            entry.Truncated = true;
    }

    private static DateTime SafeWriteTime(FileInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
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
            entry.IsReparsePoint = (info.Attributes & FileAttributes.ReparsePoint) != 0;
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

    private static BrowseEntryDto BuildFileEntry(string root, FileInfo info)
    {
        var entry = new BrowseEntryDto
        {
            Name = info.Name,
            RelativePath = Relative(root, info.FullName),
            IsDirectory = false
        };

        try
        {
            entry.SizeBytes = info.Length;
            entry.LastModifiedAt = info.LastWriteTimeUtc;
            entry.IsHidden = (info.Attributes & FileAttributes.Hidden) != 0;
            entry.IsReparsePoint = (info.Attributes & FileAttributes.ReparsePoint) != 0;
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

        /// <summary>还剩多少条目预算，供每目录配额分摊。</summary>
        public int Remaining => Math.Max(0, _maxEntries - Count);

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
