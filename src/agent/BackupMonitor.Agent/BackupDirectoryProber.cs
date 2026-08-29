using System.Diagnostics;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Recognition;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>
/// 主动探测「这台机器上像备份目录的地方」（B7）。
///
/// 与 DirectoryBrowser 的区别是方向：那一个是**定向**的（人指一个路径，返回那棵树），
/// 这一个是**发现**的（给一台机器，返回候选清单）。它回答的问题是
/// 「这台机器上还有没有别的备份没被监控起来」——而这个问题此前无法回答。
///
/// 三条边界与 DirectoryBrowser 完全一致，且更严：
///   1. **只返回候选目录本身的统计特征，不返回任何文件名**。系统主动扫全盘，
///      与人指着一个路径要看里面有什么，可接受的信息量不是一回事。
///   2. 条目数 / 深度 / 耗时三重硬上限。触顶把**已经算出来的候选原样返回**并标记，
///      而不是整个失败——半份结果远好过一句「超时了」。
///   3. 拒绝访问要说出来。「这里没有备份」和「这里我看不见」是完全不同的两件事。
///
/// 成本控制不靠调小深度，靠**命中即止 + 时间闸**：深度本身不是成本，条目数才是，
/// 而备份盘上的条目数集中在最深的一两层。命中即止（某个目录进了候选就不再下探它的子树）
/// 恰好砍掉的正是那一层——备份目录里面再套备份目录的情况极少，
/// 而它的子树恰恰是日期目录，条目最多。
/// </summary>
public sealed class BackupDirectoryProber
{
    /// <summary>目录名里出现这些词就是强信号。</summary>
    private static readonly string[] NameKeywords =
    [
        "备份", "backup", "bak", "dump", "自动备份", "db_backup", "dbbackup",
        "autobak", "sqlbackup", "数据库备份", "转储"
    ];

    /// <summary>备份类扩展名。它们在直接子文件里的占比是另一条强信号。</summary>
    private static readonly string[] BackupExtensions =
    [
        ".bak", ".trn", ".dmp", ".dbf", ".sql", ".gz", ".zip", ".7z", ".rar", ".dat", ".lst", ".tar"
    ];

    /// <summary>
    /// 一票否决的目录名。系统与程序目录里有大量 .dat/.zip，靠分数压不住，
    /// 必须直接排除——把 C:\Windows 列进候选一次，这张表的可信度就没了。
    /// </summary>
    private static readonly string[] ExcludedNames =
    [
        "windows", "winsxs", "program files", "program files (x86)", "programdata",
        "$recycle.bin", "system volume information", "recovery", "perflogs",
        "appdata", "temp", "tmp", "node_modules", ".git", "onedrive", "documents and settings"
    ];

    /// <summary>进入候选的分数线。低于它的目录不值得占人一行。</summary>
    private const int ScoreThreshold = 40;

    private readonly AgentOptions _options;
    private readonly ILogger<BackupDirectoryProber> _logger;

    public BackupDirectoryProber(IOptions<AgentOptions> options, ILogger<BackupDirectoryProber> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ProbeBackupDirsResultDto Probe(ProbeBackupDirsCommandPayload payload, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var maxDepth = Math.Clamp(payload.MaxDepth <= 0 ? 4 : payload.MaxDepth, 1, 6);
        var maxCandidates = Math.Clamp(payload.MaxCandidates <= 0 ? 20 : payload.MaxCandidates, 1, 100);
        var perDrive = TimeSpan.FromSeconds(Math.Clamp(payload.PerDriveTimeoutSeconds <= 0 ? 10 : payload.PerDriveTimeoutSeconds, 1, 120));
        var total = TimeSpan.FromSeconds(Math.Clamp(payload.TotalTimeoutSeconds <= 0 ? 30 : payload.TotalTimeoutSeconds, 1, 300));

        var result = new ProbeBackupDirsResultDto { CapturedAt = DateTime.UtcNow };
        var candidates = new List<ProbeCandidateDto>();
        var scanned = 0;

        foreach (var root in ResolveRoots(payload.Roots))
        {
            ct.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed >= total)
            {
                result.Truncated = true;
                break;
            }

            result.ScannedRoots.Add(root);
            var driveDeadline = stopwatch.Elapsed + perDrive;
            ScanDrive(root, maxDepth, driveDeadline, total, stopwatch, candidates, result, ref scanned, ct);
        }

        stopwatch.Stop();
        result.ScannedDirectories = scanned;
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        result.Candidates = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.TotalBytes)
            .Take(maxCandidates)
            .ToList();
        return result;
    }

    /// <summary>逐层广度优先，与 DirectoryBrowser 同一个理由：深度优先会把预算全花在第一条支路上。</summary>
    private void ScanDrive(
        string root, int maxDepth, TimeSpan driveDeadline, TimeSpan totalDeadline, Stopwatch stopwatch,
        List<ProbeCandidateDto> candidates, ProbeBackupDirsResultDto result, ref int scanned, CancellationToken ct)
    {
        var frontier = new List<string> { root };

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var directory in frontier)
            {
                ct.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed >= driveDeadline || stopwatch.Elapsed >= totalDeadline)
                {
                    result.Truncated = true;
                    return;
                }

                scanned++;
                var evaluated = Evaluate(directory, result);
                if (evaluated is null)
                    continue;

                if (evaluated.Candidate is not null)
                {
                    candidates.Add(evaluated.Candidate);
                    // 命中即止：备份目录里面再套备份目录极少，而它的子树恰恰是日期目录，
                    // 条目数最多。少了这一条，深度 4 的枚举量会全花在已经不需要再看的地方。
                    continue;
                }

                next.AddRange(evaluated.Children);
            }

            frontier = next;
        }
    }

    private sealed record Evaluation(ProbeCandidateDto? Candidate, List<string> Children);

    private Evaluation? Evaluate(string path, ProbeBackupDirsResultDto result)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (IsExcluded(name))
            return null;

        List<string> children;
        List<FileInfo> files;
        try
        {
            children = Directory.EnumerateDirectories(path).ToList();
            files = Directory.EnumerateFiles(path).Select(SafeFileInfo).Where(f => f is not null).Select(f => f!).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            // 拒绝访问的目录仍然要出现在结果里，标成「没有读取权限」而不是被静默跳过。
            result.DeniedPaths.Add(path);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "探测目录失败 path={Path}", path);
            result.DeniedPaths.Add(path);
            return null;
        }

        var childNames = children
            .Select(c => Path.GetFileName(c.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .ToList();

        var signals = new List<string>();
        var score = 0;

        var keyword = NameKeywords.FirstOrDefault(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (keyword is not null)
        {
            score += 40;
            signals.Add($"目录名里有「{keyword}」");
        }

        var backupFiles = files.Count(f => BackupExtensions.Contains(
            Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase));
        if (files.Count > 0)
        {
            var density = (double)backupFiles / files.Count;
            if (density >= 0.6)
            {
                score += 35;
                signals.Add($"里面 {backupFiles}/{files.Count} 个文件是备份类型（{DescribeExtensions(files)}）");
            }
            else if (density >= 0.3)
            {
                score += 15;
                signals.Add($"里面有 {backupFiles} 个备份类型的文件");
            }
        }

        var hasDateLayer = childNames.Count > 0 && DateName.IsDateLayer(childNames);
        if (hasDateLayer)
        {
            score += 35;
            signals.Add($"下一层是日期目录（{string.Join("、", childNames.Take(3))}…）");
        }

        var newest = files.Count == 0 ? (DateTime?)null : files.Max(f => SafeWriteTime(f));
        if (newest is not null && DateTime.UtcNow - newest.Value <= TimeSpan.FromDays(7))
        {
            score += 15;
            signals.Add($"最新文件是 {newest.Value:yyyy-MM-dd HH:mm}，最近还在写");
        }

        var totalBytes = files.Sum(SafeLength);
        if (totalBytes >= 1L * 1024 * 1024 * 1024)
        {
            score += 10;
            signals.Add($"体积 {FormatBytes(totalBytes)}");
        }

        if (score < ScoreThreshold)
            return new Evaluation(null, children);

        return new Evaluation(
            new ProbeCandidateDto
            {
                Path = path,
                Score = Math.Min(100, score),
                Signals = signals,
                FileCount = files.Count,
                TotalBytes = totalBytes,
                NewestFileAt = newest,
                HasDateLayer = hasDateLayer
            },
            children);
    }

    private IEnumerable<string> ResolveRoots(List<string>? requested)
    {
        if (requested is { Count: > 0 })
        {
            foreach (var root in requested)
            {
                var expanded = SafeFullPath(root);
                if (expanded is not null && Directory.Exists(expanded) && IsWithinBrowseRoots(expanded))
                    yield return expanded;
            }

            yield break;
        }

        foreach (var drive in SafeGetDrives())
        {
            var path = drive.RootDirectory.FullName;
            if (IsWithinBrowseRoots(path))
                yield return path;
        }
    }

    /// <summary>白名单与 DirectoryBrowser 共用同一条口径：那里限得住的，这里也必须限得住。</summary>
    private bool IsWithinBrowseRoots(string fullPath)
    {
        if (_options.BrowseRoots.Length == 0)
            return true;

        foreach (var allowed in _options.BrowseRoots)
        {
            var expanded = SafeFullPath(allowed);
            if (expanded is null)
                continue;
            if (fullPath.StartsWith(expanded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 一票否决的目录名。
    ///
    /// 名字为空的**不算被排除**：盘符根（`E:\`）取出来的名字就是空的，
    /// 按空名排除等于每个盘刚开始扫就被自己挡回去，探测永远返回零个候选。
    /// 根目录本身也不会命中任何关键词，分数低、照常往下走——这正是想要的。
    /// </summary>
    private static bool IsExcluded(string name) =>
        !string.IsNullOrEmpty(name)
        && ExcludedNames.Any(excluded => string.Equals(name, excluded, StringComparison.OrdinalIgnoreCase));

    private static string DescribeExtensions(List<FileInfo> files) =>
        string.Join("、", files
            .Select(f => Path.GetExtension(f.Name))
            .Where(e => !string.IsNullOrEmpty(e))
            .GroupBy(e => e, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => g.Key));

    private static FileInfo? SafeFileInfo(string path)
    {
        try { return new FileInfo(path); }
        catch (Exception) { return null; }
    }

    private static DateTime SafeWriteTime(FileInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch (Exception) { return DateTime.MinValue; }
    }

    private static long SafeLength(FileInfo info)
    {
        try { return info.Length; }
        catch (Exception) { return 0; }
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch (Exception) { return null; }
    }

    private static IEnumerable<DriveInfo> SafeGetDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

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
}
