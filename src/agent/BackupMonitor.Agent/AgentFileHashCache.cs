using System.Collections.Concurrent;
using System.Text.Json;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>
/// 文件级 SHA-256 缓存（实施方案 T5）。
///
/// 要解决的是预检里最贵的那一段：<see cref="BackupScanner"/> 的快速指纹比对粒度是
/// **整个业务单元**——单元里任何一个文件变了，这个单元里几千个文件全部重新整读一遍算全量哈希。
/// 附件库形态（几千个小文件、每次只多几个）下这笔开销几乎全是白花的。
///
/// 命中判据刻意与既有的单元级快速指纹**完全相同**：路径 + 大小 + mtime + 头尾各 64KB 的 QuickHash
/// 四项全等才复用。也就是说本类不降低现有的覆盖水平，只是把同一把尺子从单元级下放到文件级。
/// 已知且继续接受的边界仍然是那一条：「文件中段被改而 size、mtime、头尾 64KB 都没变」——
/// 正常的备份程序不会产生这种文件，而 forceFullHash（界面上的「强制完整校验」）是它的逃生门，
/// 调用方必须在 forceFullHash 时完全绕开本缓存。
///
/// 形状照抄 <see cref="AgentCandidateFileStore"/>：同一个数据目录、同样的 ACL 处理，
/// **读失败一律当作「没有」而不是抛异常**——缓存丢了只是多读一遍盘，抛异常会让整次预检失败。
/// </summary>
public sealed class AgentFileHashCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>单个任务保留的条目上限。超出后丢弃最久没见过的那些。</summary>
    private const int MaxEntriesPerTask = 50_000;

    /// <summary>条目最长保留多久没被见过就丢掉。源目录里删掉的文件靠这条自然淘汰。</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private readonly string _directory;
    private readonly ILogger<AgentFileHashCache> _logger;

    public AgentFileHashCache(IOptions<AgentOptions> options, ILogger<AgentFileHashCache> logger)
    {
        _logger = logger;
        _directory = Path.Combine(options.Value.ExpandedDataDirectory, "hashcache");
        SecureFileSystem.CreateDirectory(_directory, options.Value.EnforceAcl);
    }

    /// <summary>
    /// 打开一个任务的缓存会话：读盘一次，之后的查询都在内存里。
    ///
    /// 缓存不跨任务共享。不同任务可能配了不同的强制完整校验策略，共享会让边界难以推理；
    /// 同一个文件被两个任务覆盖时多算一遍，可以接受。
    /// </summary>
    public FileHashCacheSession Open(Guid taskId)
    {
        var path = PathFor(taskId);
        List<FileHashCacheEntry> entries = [];
        if (File.Exists(path))
        {
            try
            {
                entries = JsonSerializer.Deserialize<List<FileHashCacheEntry>>(File.ReadAllText(path), JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                // 损坏就当作空缓存：本次全部重算，行为与没有缓存时完全一致。
                _logger.LogWarning(ex, "文件哈希缓存读取失败 taskId={TaskId}，本次按无缓存处理", taskId);
                entries = [];
            }
        }

        return new FileHashCacheSession(path, entries, MaxEntriesPerTask, MaxAge, _logger);
    }

    /// <summary>缓存文件名直接用 taskId，不需要像候选清单那样再哈希一层（GUID 本身就是合法文件名）。</summary>
    private string PathFor(Guid taskId) => Path.Combine(_directory, $"{taskId:N}.json");
}

/// <summary>
/// 一条缓存记录。四个判据字段缺一不可，见 <see cref="AgentFileHashCache"/> 的说明。
/// <see cref="LastSeenUtcTicks"/> 只用于淘汰，不参与命中判定。
/// </summary>
public sealed class FileHashCacheEntry
{
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string? QuickHash { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long LastSeenUtcTicks { get; set; }
}

/// <summary>
/// 一个任务一次扫描期间的缓存会话。
///
/// 线程安全：哈希循环会并发跑（AgentOptions.MaxParallelHashes），
/// 因此查询与写入都走 <see cref="ConcurrentDictionary{TKey,TValue}"/>。
/// </summary>
public sealed class FileHashCacheSession
{
    private readonly string _path;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, FileHashCacheEntry> _entries;

    internal FileHashCacheSession(
        string path, List<FileHashCacheEntry> loaded, int maxEntries, TimeSpan maxAge, ILogger logger)
    {
        _path = path;
        _maxEntries = maxEntries;
        _maxAge = maxAge;
        _logger = logger;
        _entries = new ConcurrentDictionary<string, FileHashCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in loaded)
        {
            if (!string.IsNullOrWhiteSpace(entry.FullPath) && !string.IsNullOrWhiteSpace(entry.Sha256))
                _entries[entry.FullPath] = entry;
        }
    }

    /// <summary>本次会话有没有写入过任何东西。没有就不落盘，省掉一次无意义的写。</summary>
    private int _dirty;

    /// <summary>
    /// 命中则返回缓存的全量 SHA-256，否则 null。
    /// 四项判据（路径隐含在 key 里，另加大小、mtime、QuickHash）必须全等。
    /// </summary>
    public string? TryGet(string fullPath, long sizeBytes, DateTime lastWriteUtc, string? quickHash)
    {
        if (!_entries.TryGetValue(fullPath, out var entry))
            return null;

        if (entry.SizeBytes != sizeBytes || entry.LastWriteUtcTicks != lastWriteUtc.Ticks)
            return null;

        // QuickHash 两边都得有、且相等。缓存里没存 QuickHash 的条目（理论上不会出现，
        // 但旧格式或手工改过的文件会）一律判不命中——宁可多读一遍，不要放过一个判据。
        if (string.IsNullOrEmpty(entry.QuickHash) || !string.Equals(entry.QuickHash, quickHash, StringComparison.OrdinalIgnoreCase))
            return null;

        entry.LastSeenUtcTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _dirty, 1);
        return entry.Sha256;
    }

    /// <summary>
    /// 这次扫描见过这个文件（哪怕没有为它算哈希）。
    ///
    /// 单元级快速指纹命中时整段哈希循环会被跳过，那批文件一次 TryGet 都不会走到；
    /// 没有这个方法的话它们会在 30 天后被当成「已删除」淘汰掉，下一次扫描白白重算一遍。
    /// </summary>
    public void Touch(string fullPath)
    {
        if (_entries.TryGetValue(fullPath, out var entry))
        {
            entry.LastSeenUtcTicks = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    /// <summary>记下一个刚算出来的全量哈希。</summary>
    public void Put(string fullPath, long sizeBytes, DateTime lastWriteUtc, string? quickHash, string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
            return;

        _entries[fullPath] = new FileHashCacheEntry
        {
            FullPath = fullPath,
            SizeBytes = sizeBytes,
            LastWriteUtcTicks = lastWriteUtc.Ticks,
            QuickHash = quickHash,
            Sha256 = sha256,
            LastSeenUtcTicks = DateTime.UtcNow.Ticks
        };
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>
    /// 落盘。淘汰两道：先按「多久没见过」，再按条目数上限丢最旧的。
    /// 写失败只记日志——缓存是加速手段，写不进去下次全算就是了。
    /// </summary>
    public void Save()
    {
        if (Interlocked.CompareExchange(ref _dirty, 0, 1) == 0)
            return;

        try
        {
            var cutoff = DateTime.UtcNow.Subtract(_maxAge).Ticks;
            var keep = _entries.Values
                .Where(e => e.LastSeenUtcTicks >= cutoff)
                .OrderByDescending(e => e.LastSeenUtcTicks)
                .Take(_maxEntries)
                .ToList();

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(keep, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.Move(temp, _path, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "文件哈希缓存写入失败 path={Path}（下次扫描按无缓存处理）", _path);
        }
    }
}
