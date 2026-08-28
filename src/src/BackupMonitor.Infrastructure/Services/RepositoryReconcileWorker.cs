using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 仓库孤儿目录对帐。
///
/// 入库的最后两步是「把提交目录改名成正式目录」和「把备份集写进库」，
/// 中间隔着一次 Directory.Move。进程在这两步之间被杀（断电、蓝屏、手工重启服务），
/// 结果就是：文件好端端躺在仓库里，库里却没有任何记录。
///
/// 这个失败模式的恶劣之处不在于丢数据，而在于它安静：
/// 界面上那次备份显示为失败，你以为要重传；实际上盘上已经有一份完整的备份，
/// 而且没有任何东西会去发现它、清理它——仓库一点一点被填满，直到写不下为止。
/// UploadCommitWorker 的回滚路径处理了异常，但处理不了进程根本没机会跑回滚的情况。
///
/// 两类残骸对应两个崩溃点：
/// 一、`*.commit-xxxxxxxx` 提交临时目录 —— 崩在复制文件或写清单的过程中；
/// 二、正式命名、含 manifest.json、但 backup_sets 里查不到的目录 —— 崩在 Move 之后、
///     SaveChangesAsync 之前。清单先于改名落盘，所以「有清单」就意味着文件已经齐了。
///
/// **只报不删。** 这里面每一个目录都可能是一份货真价实的备份，
/// 删错了没有第二份。程序的职责是让人知道它们存在，处置由人决定。
///
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁），幂等。
/// </summary>
public class RepositoryReconcileWorker : BackgroundService
{
    /// <summary>执行间隔（秒）system_settings 键（V025 新增，默认 21600 即 6 小时）</summary>
    public const string IntervalKey = "repository_reconcile_interval_seconds";

    /// <summary>
    /// 判定为孤儿前的静默期（小时）system_settings 键（V025 新增，默认 24）。
    ///
    /// 正在进行的入库本来就长着一副孤儿相：提交目录已经建好、库里还没有行。
    /// 大备份集的复制可能跑很久，静默期必须宽到能覆盖最慢的一次入库，
    /// 否则每轮巡检都会把正在干活的目录报成残骸。
    /// </summary>
    public const string GraceHoursKey = "repository_reconcile_grace_hours";

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:repository_reconcile";

    /// <summary>锁 TTL：须大于单轮最坏耗时（一次全仓库目录遍历）</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(30);

    /// <summary>告警键：仓库级问题，与具体客户端无关</summary>
    public const string AlertKey = "repository:orphan_directories";

    /// <summary>告警正文里最多列几条路径，其余只给总数——告警不是报表</summary>
    private const int MaxListedPaths = 20;

    /// <summary>
    /// 目录遍历深度上限。仓库布局是 主机名/任务名[/业务单元]/版本目录，
    /// 也就是最深 4 层；给到 6 层留出手工整理过目录的余量，同时保证
    /// 仓库根被误配成盘符根时不会把整块盘走一遍。
    /// </summary>
    private const int MaxDepth = 6;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RepositoryReconcileWorker> _logger;

    public RepositoryReconcileWorker(IServiceScopeFactory scopeFactory, ILogger<RepositoryReconcileWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("仓库孤儿目录对帐工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 21600;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                // 下限 5 分钟：这一轮要走一遍整个仓库目录树，配得太密只是白白搅动磁盘
                intervalSeconds = Math.Max(300, await settings.GetIntAsync(IntervalKey, 21600, stoppingToken));

                var scheduledLock = scope.ServiceProvider.GetRequiredService<IScheduledLockService>();
                if (await scheduledLock.TryAcquireAsync(LockKey, LockTtl, stoppingToken))
                {
                    try
                    {
                        await RunPassAsync(scope, stoppingToken);
                    }
                    finally
                    {
                        await scheduledLock.ReleaseAsync(LockKey, CancellationToken.None);
                    }
                }
                // 未抢到锁说明其他实例正在对帐，本轮跳过
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "仓库孤儿目录对帐轮询异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 单轮对帐。
    ///
    /// 公开而不是私有，是为了让集成测试能确定性地驱动一轮——
    /// 靠 StartAsync 等首轮跑完再轮询断言，测试会变成对时序的赌博。
    /// </summary>
    public async Task<IReadOnlyList<OrphanDirectory>> RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();

        string repositoryRoot;
        try
        {
            repositoryRoot = await storage.GetRepositoryRootAsync(ct);
        }
        catch (Exception ex)
        {
            // 仓库路径配错是另一回事，SystemWatchdogWorker 会报它；
            // 这里只要不把对帐工作器整个拖死就行。
            _logger.LogWarning(ex, "仓库根目录解析失败，本轮跳过对帐");
            return [];
        }

        if (!Directory.Exists(repositoryRoot))
        {
            _logger.LogWarning("仓库根目录不存在，本轮跳过对帐：{Root}", repositoryRoot);
            return [];
        }

        var graceHours = Math.Max(1, await settings.GetIntAsync(GraceHoursKey, 24, ct));
        var cutoff = DateTime.UtcNow.AddHours(-graceHours);

        // 已知目录集合取自 backup_sets 全量而不是只取 available：
        // 回收站里的、校验失败的、待删除的，它们的目录都还在盘上，都不是孤儿。
        var knownPaths = await db.BackupSets.AsNoTracking()
            .Where(s => s.RepositoryPath != null)
            .Select(s => s.RepositoryPath!)
            .ToListAsync(ct);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in knownPaths)
        {
            // 库里存的是入库当时拼出来的路径，可能带尾部分隔符或相对片段；
            // 规范化之后再比，否则同一个目录会因为写法不同被判成孤儿。
            try { known.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger.LogWarning("备份集仓库路径无法规范化，对帐时跳过：{Path}", path);
            }
        }

        var orphans = ScanOrphans(repositoryRoot, known, cutoff);

        if (orphans.Count == 0)
        {
            await alerting.RecoverAsync(AlertKey, ct);
            return orphans;
        }

        var totalBytes = orphans.Sum(o => o.SizeBytes);
        var listed = orphans.OrderByDescending(o => o.SizeBytes).Take(MaxListedPaths).ToList();
        var message = string.Join('\n', listed.Select(o =>
            $"· {o.Path}（{FormatBytes(o.SizeBytes)}，{o.Kind switch
            {
                OrphanKind.CommitTemp => "入库中途残留的临时目录",
                _ => "文件已入仓但数据库无记录"
            }}）"))
            + (orphans.Count > listed.Count ? $"\n… 另有 {orphans.Count - listed.Count} 个未列出" : string.Empty)
            + "\n\n这些目录不会被自动删除。请确认其中是否有需要保留的备份，再手工处置。";

        await alerting.RaiseAsync(
            AlertKey,
            AlertLevel.Warning,
            "storage",
            $"备份库里有 {orphans.Count} 个查无记录的目录（共 {FormatBytes(totalBytes)}）",
            message,
            ct: ct);

        _logger.LogWarning("仓库孤儿目录对帐：发现 {Count} 个孤儿目录，共 {Bytes} 字节", orphans.Count, totalBytes);
        return orphans;
    }

    /// <summary>
    /// 遍历仓库目录树找出两类残骸。
    ///
    /// 用显式栈而不是 Directory.EnumerateDirectories(SearchOption.AllDirectories)：
    /// 后者遇到一个无权限的子目录就整个抛出，一个坏目录会让整轮对帐什么都查不出来。
    ///
    /// 公开是为了让测试能只对着真实目录树验判定规则，不必拉起数据库——
    /// 这一段的全部风险都在「哪些目录算孤儿」上，那是纯文件系统的事。
    /// </summary>
    public List<OrphanDirectory> ScanOrphans(string repositoryRoot, HashSet<string> known, DateTime cutoff)
    {
        var orphans = new List<OrphanDirectory>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)), 0));

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();

            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                _logger.LogWarning(ex, "仓库子目录无法枚举，对帐时跳过：{Dir}", current);
                continue;
            }

            foreach (var child in children)
            {
                var kind = ClassifyDirectory(child, known);
                if (kind is null)
                {
                    // 不是残骸也不是已知备份集：可能是中间层（主机名 / 任务名 / 业务单元），继续往下走
                    if (depth + 1 < MaxDepth)
                        pending.Push((child, depth + 1));
                    continue;
                }

                // 已知备份集目录：整棵子树都属于它，不再往下钻——
                // 备份集内部的子目录是备份内容本身，不是仓库结构。
                if (kind == OrphanKind.None)
                    continue;

                try
                {
                    if (Directory.GetLastWriteTimeUtc(child) > cutoff)
                        continue;   // 静默期内，可能是正在进行的入库
                    orphans.Add(new OrphanDirectory(child, kind.Value, DirectorySize(child)));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
                {
                    _logger.LogWarning(ex, "孤儿目录信息读取失败，对帐时跳过：{Dir}", child);
                }
            }
        }

        return orphans;
    }

    /// <summary>
    /// 判断一个目录属于哪一类：null 表示「还看不出来，继续往下找」，
    /// None 表示「是已知备份集，到此为止」，其余是两类残骸。
    /// </summary>
    private static OrphanKind? ClassifyDirectory(string path, HashSet<string> known)
    {
        var name = Path.GetFileName(path);

        // 提交临时目录：UploadCommitWorker 用 finalPath + ".commit-xxxxxxxx" 命名，
        // 正常情况下它的寿命只有一次 Directory.Move 那么长。
        if (name.Contains(".commit-", StringComparison.Ordinal))
            return OrphanKind.CommitTemp;

        var normalized = Path.TrimEndingDirectorySeparator(path);
        if (known.Contains(normalized))
            return OrphanKind.None;

        // 清单先于改名落盘（P1-6），所以「有 manifest.json」等价于
        // 「这是一个已经完成的备份集目录」。库里没有它，就是崩在两步之间了。
        return File.Exists(Path.Combine(path, "manifest.json")) ? OrphanKind.Missing : null;
    }

    private long DirectorySize(string path)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.GetFiles(current))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or IOException)
                    {
                        // 单个文件读不到大小不影响「这里有个孤儿目录」这个结论
                    }
                }
                foreach (var dir in Directory.GetDirectories(current))
                    pending.Push(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                _logger.LogDebug(ex, "孤儿目录大小统计跳过子目录：{Dir}", current);
            }
        }

        return total;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}

/// <summary>孤儿目录的成因，决定告警里怎么向人解释这个目录是什么</summary>
public enum OrphanKind
{
    /// <summary>不是孤儿：库里有对应的备份集</summary>
    None,

    /// <summary>入库中途留下的 *.commit-xxxxxxxx 临时目录</summary>
    CommitTemp,

    /// <summary>正式命名且含清单，但 backup_sets 里查不到</summary>
    Missing
}

/// <summary>一条对帐结果</summary>
public record OrphanDirectory(string Path, OrphanKind Kind, long SizeBytes);
