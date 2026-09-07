using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>管理端传输进度查询</summary>
public interface IUploadProgressService
{
    /// <summary>当前在传的会话，按剩余量从大到小排——最该盯着的那条排在最上面。</summary>
    Task<IReadOnlyList<UploadProgressDto>> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// 全局上传闸的当前状态（D3）：几个在传、几个在排队、上限是多少。
    /// 不显示的话，限流的表现就是「点了没反应」——那比不限流更糟。
    /// </summary>
    Task<UploadQueueStatusDto> GetQueueStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// 最近结束的传输，最新的在前。
    ///
    /// 「传输中」只显示在途会话，传完就消失，于是没传成的那些（failed / cancelled /
    /// expired）在界面上无处可查——它们不入备份集，也不在传输中。耗时、平均速度、
    /// 重试次数同理：只有会话上才有，而它们正是判断链路好坏的依据。
    /// </summary>
    Task<PagedResult<FinishedTransferDto>> GetRecentFinishedAsync(
        PagedQuery query, CancellationToken ct = default);
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
    private readonly SystemSettingsProvider _settings;

    public UploadProgressService(AppDbContext db, UploadRateSampler sampler, SystemSettingsProvider settings)
    {
        _db = db;
        _sampler = sampler;
        _settings = settings;
    }

    public async Task<UploadQueueStatusDto> GetQueueStatusAsync(CancellationToken ct = default)
    {
        // 「在传」用 Active 而不是 InFlight：占着全局名额的口径必须与
        // SequentialExecutionWorker 放行时数的那一个完全一致，否则界面上显示
        // 「3 个在传，上限 4」而队列却不放行，人无从判断系统是不是卡住了。
        var active = await _db.UploadSessions
            .CountAsync(s => UploadSessionStatuses.Active.Contains(s.Status), ct);

        var globalLimit = Math.Clamp(
            await _settings.GetIntAsync(SequentialExecutionWorker.GlobalUploadLimitKey, 4, ct), 1, 64);

        // 两种「排队中」必须分开数，它们等的根本不是一回事：
        //   一、等全局上传名额——只有上传项会被这道闸挡住（预检项一个名额都不占），
        //       而且只有在名额真的满了的时候；
        //   二、等本次执行自己的并发度——前一项还没跑完，这是「严格按顺序」的正常表现。
        // 混成一个数的后果是界面显示「0 个正在传 / 2 个排队中 · 有空余名额」，
        // 读起来就是系统卡住了，而实际上那两项在正常排队。
        var pending = await _db.Set<Core.Entities.Execution.ExecutionRunItem>()
            .Where(i => i.Status == ExecutionItemStatus.Pending
                && (i.Run.Status == BatchStatus.Pending || i.Run.Status == BatchStatus.Running))
            .Select(i => new { i.CommandType, RunId = i.RunId, i.Run.MaxConcurrent })
            .ToListAsync(ct);

        var slotFull = active >= globalLimit;
        var runningPerRun = await _db.Set<Core.Entities.Execution.ExecutionRunItem>()
            .Where(i => i.Status == ExecutionItemStatus.Running)
            .GroupBy(i => i.RunId)
            .Select(g => new { RunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.RunId, g => g.Count, ct);

        var waitingForSlot = 0;
        var waitingInRun = 0;
        foreach (var item in pending)
        {
            // 判据与执行器放行时、与队列名单（ExecutionQueueService.GetQueueAsync）
            // 用的是同一个：这三处一旦分家，界面上的数字就会和实际放行对不上。
            var wait = ExecutionQueueWaits.Classify(
                runningPerRun.TryGetValue(item.RunId, out var n) ? n : 0,
                item.MaxConcurrent,
                item.CommandType,
                slotFull);
            if (wait == ExecutionQueueWaits.WaitingForUploadSlot)
                waitingForSlot++;
            else
                waitingInRun++;
        }

        return new UploadQueueStatusDto
        {
            ActiveUploads = active,
            QueuedItems = pending.Count,
            WaitingForUploadSlot = waitingForSlot,
            WaitingInRun = waitingInRun,
            GlobalLimit = globalLimit
        };
    }

    public async Task<PagedResult<FinishedTransferDto>> GetRecentFinishedAsync(
        PagedQuery query, CancellationToken ct = default)
    {
        var sessions = _db.UploadSessions.AsNoTracking()
            .Where(s => UploadSessionStatuses.Terminal.Contains(s.Status));

        var total = await sessions.LongCountAsync(ct);

        var rows = await sessions
            // 按结束时刻排，不是创建时刻：人要看的是「最近发生了什么」。
            // 终态会话理应都有 CompletedAt，早期数据里可能为空，用 CreatedAt 兜底，
            // 否则那几条会永远沉在最后一页。
            .OrderByDescending(s => s.CompletedAt ?? s.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(s => new
            {
                s.Id,
                s.ClientId,
                ClientDisplayName = s.Client.DisplayName,
                s.TaskId,
                TaskName = s.Task.Name,
                BusinessUnitName = s.CandidateBackupSet.BusinessUnit != null
                    ? s.CandidateBackupSet.BusinessUnit.DisplayName
                    : null,
                s.Status,
                s.TotalFiles,
                s.TotalBytes,
                s.UploadedBytes,
                s.RetryCount,
                s.ErrorCode,
                s.ErrorMessage,
                s.StartedAt,
                s.CompletedAt,
                s.CreatedAt,
                // 只认还活着的备份集。软删的行还在，把它算进来等于告诉人
                // 「这次传输的成果还在」，而那份备份恰恰是刚被删掉的那一份。
                BackupSetCode = _db.BackupSets
                    .Where(b => b.UploadSessionId == s.Id && BackupSetStatuses.Live.Contains(b.Status))
                    .Select(b => b.BackupSetCode)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var since = r.StartedAt ?? r.CreatedAt;
            var duration = r.CompletedAt is null ? (TimeSpan?)null : r.CompletedAt.Value - since;

            // 耗时不足 1 秒时不给速度：几十毫秒的除数会算出一个荒唐的数，
            // 而那种会话（比如幂等复用的）本来就没有「速度」可言。
            long? average = duration is { TotalSeconds: >= 1 } && r.UploadedBytes > 0
                ? (long)(r.UploadedBytes / duration.Value.TotalSeconds)
                : null;

            return new FinishedTransferDto
            {
                SessionId = r.Id,
                ClientId = r.ClientId,
                ClientDisplayName = r.ClientDisplayName,
                TaskId = r.TaskId,
                TaskName = r.TaskName,
                BusinessUnitName = r.BusinessUnitName,
                Status = EnumMapping.ToSnakeCase(r.Status),
                TotalFiles = r.TotalFiles,
                TotalBytes = r.TotalBytes,
                UploadedBytes = r.UploadedBytes,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                DurationSeconds = duration is null ? null : (long)Math.Max(0, duration.Value.TotalSeconds),
                AverageBytesPerSecond = average,
                RetryCount = r.RetryCount,
                ErrorCode = r.ErrorCode,
                ErrorMessage = r.ErrorMessage,
                BackupSetCode = r.BackupSetCode
            };
        }).ToList();

        return PagedResult<FinishedTransferDto>.Create(items, total, query.Page, query.PageSize);
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
                // 会话 → 候选备份集 → 业务单元。左连接：老数据里候选可能没挂业务单元，
                // 那时给 null 让界面退回只显示任务名，而不是让整张表查不出来。
                BusinessUnitName = s.CandidateBackupSet.BusinessUnit != null
                    ? s.CandidateBackupSet.BusinessUnit.DisplayName
                    : null,
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
                BusinessUnitName = r.BusinessUnitName,
                Status = EnumMapping.ToSnakeCase(r.Status),
                TotalFiles = r.TotalFiles,
                TotalBytes = r.TotalBytes,
                UploadedBytes = r.UploadedBytes,
                Percent = r.TotalBytes > 0
                    ? Math.Clamp(Math.Round((decimal)r.UploadedBytes * 100 / r.TotalBytes, 2), 0, 100)
                    : 0,
                BytesPerSecond = rate,
                // 一分钟的走势（实施方案 U1）：一个瞬时数字分不出「慢」和「正在变慢」，
                // 而这两者的处置完全不同——前者等着就好，后者要去查链路。
                RecentRates = _sampler.HistoryOf(r.Id),
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

    /// <summary>
    /// 每个会话保留多少个历史速率点。5 秒轮询 × 12 ≈ 覆盖最近一分钟，
    /// 正好是「链路正常 / 正在退化 / 已经卡了」这个判断需要的尺度。
    /// 内存开销是 12 个 long × 并发会话数，可以忽略。
    /// </summary>
    private const int HistoryPoints = 12;

    private readonly Dictionary<Guid, Sample> _samples = [];
    private readonly Dictionary<Guid, Queue<long>> _history = [];
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
            //
            // 采样窗口内计数没动就返回 null，让调用方退回全程平均值。
            // 返回 0 的话 `Observe(...) ?? averageRate` 这个回退根本不生效（0 不是 null），
            // 一次正常的采样间隔就把「正在传」显示成「0 B/s、预计剩余 —」。
            // 分块 8MB 才推进一次计数，而界面 5 秒一刷，采到 0 是常态而不是异常。
            var delta = Math.Max(0, uploadedBytes - previous.Bytes);
            long? rate = delta > 0 ? (long)(delta / gap.TotalSeconds) : null;
            _samples[sessionId] = new Sample(uploadedBytes, nowUtc, rate);

            // 只把真正采到的点记进历史（实施方案 U1）。
            //
            // rate 为 null 表示「这个窗口里计数没动」——8MB 才推进一次而界面 5 秒一刷，
            // 采到 null 是常态。把它当 0 记进去会画出一条上下打摆的锯齿，
            // 而那条线要回答的是趋势，不是采样噪声。
            if (rate is not null)
            {
                if (!_history.TryGetValue(sessionId, out var points))
                    _history[sessionId] = points = new Queue<long>(HistoryPoints);
                points.Enqueue(rate.Value);
                while (points.Count > HistoryPoints)
                    points.Dequeue();
            }

            return rate;
        }
    }

    /// <summary>
    /// 最近若干次采到的瞬时速率，旧→新。
    ///
    /// 不足两点时返回空：调用方据此显示「—」而不是画一条从 0 冲上来的假曲线。
    /// 补零是这里最容易犯的错——它会让刚开始的传输看起来像刚刚提速。
    /// </summary>
    public IReadOnlyList<long> HistoryOf(Guid sessionId)
    {
        lock (_gate)
        {
            return _history.TryGetValue(sessionId, out var points) && points.Count >= 2
                ? points.ToArray()
                : [];
        }
    }

    /// <summary>丢掉已经不在传的会话，否则这张表会随会话数一直涨。</summary>
    public void Forget(IReadOnlyCollection<Guid> liveSessionIds)
    {
        lock (_gate)
        {
            if (_samples.Count == 0 && _history.Count == 0) return;
            foreach (var stale in _samples.Keys.Where(k => !liveSessionIds.Contains(k)).ToList())
                _samples.Remove(stale);
            // 历史也要一起清，否则这张表会随会话数一直涨——和 _samples 是同一个理由。
            foreach (var stale in _history.Keys.Where(k => !liveSessionIds.Contains(k)).ToList())
                _history.Remove(stale);
        }
    }

    private readonly record struct Sample(long Bytes, DateTime AtUtc, long? LastRate);
}
