using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>管理端传输进度查询</summary>
public interface IUploadProgressService
{
    /// <summary>当前在传的会话，按剩余量从大到小排——最该盯着的那条排在最上面。</summary>
    Task<IReadOnlyList<UploadProgressDto>> GetActiveAsync(CancellationToken ct = default);
}

/// <summary>
/// 把 upload_sessions 里已有的进度数字翻译成「还要多久」。
///
/// 只读，不写库，也不下发任何指令：备份正在跑的时候，看进度这个动作本身
/// 绝不能对备份产生任何影响。
/// </summary>
public class UploadProgressService : IUploadProgressService
{
    /// <summary>与 ClientAdminService.ActiveUploadStatuses 保持一致：界面上「在传」的口径只能有一个。</summary>
    private static readonly UploadStatus[] ActiveStatuses = UploadSessionStatuses.InFlight;

    /// <summary>
    /// 卡死判定的下限。低于这个值会把「正在传一个大分块」误报成卡死——
    /// 8MB 的分块在 100KB/s 的链路上本来就要传 80 秒，中途 last_activity_at 是不动的。
    /// </summary>
    private static readonly TimeSpan MinStallThreshold = TimeSpan.FromSeconds(180);

    /// <summary>卡死判定的上限。链路再慢，静默半小时也该让人去看一眼了。</summary>
    private static readonly TimeSpan MaxStallThreshold = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _db;
    private readonly UploadRateSampler _sampler;

    public UploadProgressService(AppDbContext db, UploadRateSampler sampler)
    {
        _db = db;
        _sampler = sampler;
    }

    public async Task<IReadOnlyList<UploadProgressDto>> GetActiveAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var rows = await _db.UploadSessions
            .AsNoTracking()
            .Where(s => ActiveStatuses.Contains(s.Status))
            .Select(s => new
            {
                s.Id,
                s.ClientId,
                s.Client.Hostname,
                ClientDisplayName = s.Client.DisplayName,
                s.TaskId,
                TaskName = s.Task.Name,
                s.Status,
                s.TotalFiles,
                s.TotalBytes,
                s.UploadedBytes,
                s.ChunkSizeBytes,
                s.StartedAt,
                s.LastActivityAt,
                s.CreatedAt
            })
            .ToListAsync(ct);

        var live = rows.Select(r => r.Id).ToHashSet();
        _sampler.Forget(live);

        var items = new List<UploadProgressDto>(rows.Count);
        foreach (var r in rows)
        {
            // StartedAt 在会话真正开始收数据前是空的，此时用 CreatedAt 兜底，
            // 否则「刚创建还没传」的会话会算出一个无穷大的速度。
            var since = r.StartedAt ?? r.CreatedAt;
            var elapsed = now - since;

            // 全程平均：会话建出来的那一刻 elapsed 可能是 0，除之前先兜住。
            long? averageRate = elapsed.TotalSeconds >= 1 && r.UploadedBytes > 0
                ? (long)(r.UploadedBytes / elapsed.TotalSeconds)
                : null;

            var rate = _sampler.Observe(r.Id, r.UploadedBytes, now) ?? averageRate;

            var remaining = Math.Max(0, r.TotalBytes - r.UploadedBytes);
            var idle = r.LastActivityAt is null ? elapsed : now - r.LastActivityAt.Value;

            items.Add(new UploadProgressDto
            {
                SessionId = r.Id,
                ClientId = r.ClientId,
                Hostname = r.Hostname,
                ClientDisplayName = r.ClientDisplayName,
                TaskId = r.TaskId,
                TaskName = r.TaskName,
                Status = EnumMapping.ToSnakeCase(r.Status),
                TotalFiles = r.TotalFiles,
                TotalBytes = r.TotalBytes,
                UploadedBytes = r.UploadedBytes,
                Percent = r.TotalBytes > 0
                    ? Math.Clamp(Math.Round((decimal)r.UploadedBytes * 100 / r.TotalBytes, 2), 0, 100)
                    : 0,
                BytesPerSecond = rate,
                // 速度为 0 时不给剩余时间。显示「剩余 0 秒」比不显示更糟：
                // 它看起来像马上就好了，实际是一点都没在动。
                EtaSeconds = rate is > 0 ? (long)(remaining / rate.Value) : null,
                StartedAt = r.StartedAt,
                LastActivityAt = r.LastActivityAt,
                IdleSeconds = (long)Math.Max(0, idle.TotalSeconds),
                Stalled = idle > StallThreshold(r.ChunkSizeBytes, averageRate)
            });
        }

        // 剩余字节最多的排最前：它决定整批备份什么时候结束。
        return items
            .OrderByDescending(i => i.Stalled)
            .ThenByDescending(i => i.TotalBytes - i.UploadedBytes)
            .ToList();
    }

    /// <summary>
    /// 卡死阈值随链路实测速度放大：按当前平均速度传完一个分块要多久，给三倍余量。
    /// 用全程平均而不是瞬时速度，是因为真卡住时瞬时速度会掉到 0，
    /// 拿它算阈值会让阈值自己涨到无穷大，永远报不出卡死。
    ///
    /// 公开是为了能单测：这条判定错在哪一边都要命——判松了卡死永远不报，
    /// 判紧了每条慢传输都被标成故障，而后者会让人很快学会无视这个标记。
    /// </summary>
    public static TimeSpan StallThreshold(int chunkSizeBytes, long? averageRate)
    {
        if (averageRate is not > 0)
            return MinStallThreshold;

        var perChunk = TimeSpan.FromSeconds((double)chunkSizeBytes / averageRate.Value * 3);
        if (perChunk < MinStallThreshold) return MinStallThreshold;
        return perChunk > MaxStallThreshold ? MaxStallThreshold : perChunk;
    }
}

/// <summary>
/// 会话速度采样器（单例，进程内）。
///
/// 库里只有累计字节数，算不出「现在多快」，得有个地方记住上一次看到的是多少。
/// 建一张表来存这个不值得——它是纯展示数据，进程重启后重新采一次就有了，
/// 而且这套系统是单机部署，没有跨进程共享的必要。
/// </summary>
public sealed class UploadRateSampler
{
    /// <summary>
    /// 两次采样至少要隔这么久才更新基准。
    /// 界面轮询间隔不受控（多开几个浏览器就会有毫秒级的相邻两次请求），
    /// 拿一个极短的时间差去除会得出剧烈跳动的速度，看着像故障其实是采样噪声。
    /// </summary>
    private static readonly TimeSpan MinSampleGap = TimeSpan.FromSeconds(3);

    private readonly Dictionary<Guid, Sample> _samples = [];
    private readonly object _gate = new();

    /// <summary>
    /// 记下这次观测并给出速度。第一次观测没有可比对象，返回 null 让调用方退回全程平均。
    /// 距上次不足 MinSampleGap 时不更新基准，直接复用上次算出的速度。
    /// </summary>
    public long? Observe(Guid sessionId, long uploadedBytes, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_samples.TryGetValue(sessionId, out var previous))
            {
                _samples[sessionId] = new Sample(uploadedBytes, nowUtc, null);
                return null;
            }

            var gap = nowUtc - previous.AtUtc;
            if (gap < MinSampleGap)
                return previous.LastRate;

            // 续传会话重建后 uploaded_bytes 可能变小，此时差值为负，按 0 处理。
            var delta = Math.Max(0, uploadedBytes - previous.Bytes);
            var rate = (long)(delta / gap.TotalSeconds);
            _samples[sessionId] = new Sample(uploadedBytes, nowUtc, rate);
            return rate;
        }
    }

    /// <summary>丢掉已经不在传的会话，否则这张表会随会话数一直涨。</summary>
    public void Forget(IReadOnlyCollection<Guid> liveSessionIds)
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return;
            foreach (var stale in _samples.Keys.Where(k => !liveSessionIds.Contains(k)).ToList())
                _samples.Remove(stale);
        }
    }

    private readonly record struct Sample(long Bytes, DateTime AtUtc, long? LastRate);
}
