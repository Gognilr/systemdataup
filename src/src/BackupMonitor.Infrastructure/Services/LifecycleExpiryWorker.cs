using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 生命周期到期巡检（审计 A-03 / A-04 / D-05）。
///
/// 系统里几张表的状态机都定义了「过期」这一格，却没有任何生产者——
/// upload_sessions.expired、commands.expired 都是只读不写。缺的不是判断逻辑，
/// 是「时间推动状态转移」这件事本身没人做，这个 worker 就是那个人。
///
/// 不做这件事的后果是一条完整的故障链：Agent 中途失败留下永久 uploading 会话，
/// 攒够 max_concurrent_uploads 之后这台客户端所有上传永久 409；同时那些会话的
/// .part 文件谁也不删，暂存盘填满后全体客户端 507。
///
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁），幂等。
/// </summary>
public class LifecycleExpiryWorker : BackgroundService
{
    /// <summary>执行间隔（秒）system_settings 键（V022 新增，默认 300）</summary>
    public const string IntervalKey = "lifecycle_expiry_interval_seconds";

    /// <summary>终态会话暂存目录的保留期（小时）system_settings 键（V022 新增，默认 24）</summary>
    public const string StagingRetentionKey = "staging_cleanup_retention_hours";

    /// <summary>上传会话超时（秒）system_settings 键（V001 已种子，此前赋值后零引用）</summary>
    public const string SessionTimeoutKey = "upload_session_timeout_seconds";

    /// <summary>
    /// 会话超时的兜底默认值（秒）。6 小时，与 V029 种进 system_settings 的值一致。
    /// 它不是「一次上传最多能传多久」，而是「多久没有任何动静才认定它废了」——
    /// 原来的 1 小时对 6GB 级别的备份集太窄，慢链路或白天限速就会在还能续传的时候先判死。
    /// </summary>
    public const int DefaultSessionTimeoutSeconds = 21600;

    /// <summary>
    /// retry_wait 会话的回收时限（秒）system_settings 键。
    ///
    /// retry_wait 保留的是断点，不是正在进行的传输——Agent 上传出错后把会话留在这个状态，
    /// 好让下一次重试能续上。拿「多久没动静才算废了」的 6 小时去卡它，
    /// 一次失败就把这台机器锁住半天：它既占单客户端上限（默认 2）也占全局上限（默认 4），
    /// 而 U8 这类一台机器 18 个账套的任务，只要两个单元失败就再也传不动第三个。
    /// </summary>
    public const string RetryWaitTimeoutKey = "upload_retry_wait_timeout_seconds";

    /// <summary>retry_wait 回收时限的兜底默认值（秒）：30 分钟</summary>
    public const int DefaultRetryWaitTimeoutSeconds = 1800;

    /// <summary>指令领取后未上报开始的退回阈值（秒）system_settings 键（V022 新增，默认 900）</summary>
    public const string ClaimTimeoutKey = "command_claim_timeout_seconds";

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:lifecycle_expiry";

    /// <summary>锁 TTL：须大于单轮最坏耗时（清理段最多删 200 个会话目录）</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(10);

    /// <summary>单轮暂存清理条数上限，避免一轮删太多把巡检卡在锁 TTL 之外</summary>
    private const int CleanupBatchSize = 200;

    /// <summary>
    /// 还没终结、因而要参与超时判定的四个状态，与 UploadSessionService.ActiveStatuses 一致。
    ///
    /// paused 刻意留在里面：它不再是可写状态（待办方案 E 把它从写入白名单里拿掉了），
    /// 但它仍然占着暂存空间和并发额度。有人点了暂停之后忘掉，超时线就是唯一的兜底。
    /// </summary>
    private static readonly UploadStatus[] WritableStatuses = UploadSessionStatuses.Active;

    /// <summary>会话终态：这些状态的暂存目录不会再被写，可以清</summary>
    private static readonly UploadStatus[] TerminalStatuses = UploadSessionStatuses.Terminal;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LifecycleExpiryWorker> _logger;

    public LifecycleExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<LifecycleExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("生命周期到期巡检工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 300;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                intervalSeconds = Math.Max(30, await settings.GetIntAsync(IntervalKey, 300, stoppingToken));

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
                // 未抢到锁说明其他实例正在巡检，本轮跳过
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生命周期到期巡检轮询异常");
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
    /// 单轮巡检。三段之间有先后依赖：会话超时先把僵尸会话推到终态，
    /// 暂存清理才有东西可清（同一轮不会清到刚过期的，保留期给排障留了窗口）。
    ///
    /// 公开而不是私有，是为了让集成测试能确定性地驱动一轮巡检——
    /// 靠 StartAsync 等首轮跑完再轮询断言，测试会变成对时序的赌博。
    /// </summary>
    public async Task RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        await ExpireStaleSessionsAsync(scope, ct);
        await CleanupStagingAsync(scope, ct);
        await RecycleStaleCommandsAsync(scope, ct);
    }

    /// <summary>
    /// A-03：上传会话超时。
    ///
    /// 刻意不用 ExecuteUpdateAsync 批量改——批量 UPDATE 拿不到「哪些会话刚刚过期」
    /// 这个集合，第二段的暂存清理就接不上（这和 SystemWatchdogWorker 里客户端存活判定
    /// 不用批量 UPDATE 是同一个理由：那边要拿到集合才发得出告警）。
    /// 活动会话的数量级是「客户端数 × max_concurrent_uploads」，一次加载没有性能问题。
    /// </summary>
    private async Task ExpireStaleSessionsAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();

        // 下限 60 秒：配得太小会把正在正常传输、只是块间隔略长的会话误杀。
        var timeoutSeconds = Math.Max(60, await settings.GetIntAsync(SessionTimeoutKey, DefaultSessionTimeoutSeconds, ct));

        // retry_wait 单独一条更短的线。这两个数问的不是同一个问题：
        // 6 小时问的是「一个还在传的会话多久没动静才算废了」，
        // 30 分钟问的是「一个只剩断点的会话还值不值得替它占着名额」。
        var retryWaitSeconds = Math.Max(60,
            await settings.GetIntAsync(RetryWaitTimeoutKey, DefaultRetryWaitTimeoutSeconds, ct));

        var now = DateTime.UtcNow;
        var deadline = now.AddSeconds(-timeoutSeconds);
        var retryWaitDeadline = now.AddSeconds(-retryWaitSeconds);

        // 用 LastActivityAt 判定：它在每个分块写入时刷新（UploadChunkAsync），
        // 是「这个会话还有没有人在推进」的唯一可靠依据。
        // 为空（建了会话一个块都没传）时退回 CreatedAt。
        var stale = await db.UploadSessions
            .Where(s => WritableStatuses.Contains(s.Status)
                        && (s.Status == UploadStatus.RetryWait
                            ? (s.LastActivityAt ?? s.CreatedAt) < retryWaitDeadline
                            : (s.LastActivityAt ?? s.CreatedAt) < deadline))
            .ToListAsync(ct);

        if (stale.Count == 0)
            return;

        foreach (var session in stale)
        {
            var applied = session.Status == UploadStatus.RetryWait ? retryWaitSeconds : timeoutSeconds;
            session.Status = UploadStatus.Expired;
            session.ErrorCode = "SESSION_TIMEOUT";
            session.ErrorMessage = $"超过 {applied} 秒无分块写入，已自动过期";
            session.CompletedAt = now;
            session.UpdatedAt = now;
        }

        // 暂存目录不在这里删：staging_cleanup_retention_hours 那条保留期照旧，
        // 30 分钟内 Agent 重试时仍然能从断点续上——这条更短的线要收回的是名额，不是数据。
        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "上传会话超时回收 {Count} 个（阈值 {Timeout} 秒，retry_wait {RetryWait} 秒）",
            stale.Count, timeoutSeconds, retryWaitSeconds);
    }

    /// <summary>
    /// A-04：终态会话的暂存目录清理。
    ///
    /// StagingPath 非空 = 还没清过（CreateSessionAsync 写入，清完置 null），
    /// 因此这一步天然幂等，不必新增「已清理」列。
    /// </summary>
    private async Task CleanupStagingAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();

        var retentionHours = Math.Max(1, await settings.GetIntAsync(StagingRetentionKey, 24, ct));
        var cleanupBefore = DateTime.UtcNow.AddHours(-retentionHours);

        // Committed 也在内：入库成功时已经清过一次，但那次是 try/catch 吞掉异常的，
        // 这里兜底。保留期是给排障留的窗口——失败的会话现场别马上毁掉。
        var cleanable = await db.UploadSessions
            .Where(s => TerminalStatuses.Contains(s.Status)
                        && s.StagingPath != null
                        && (s.CompletedAt ?? s.UpdatedAt) < cleanupBefore)
            .Take(CleanupBatchSize)
            .ToListAsync(ct);

        if (cleanable.Count > 0)
        {
            foreach (var session in cleanable)
            {
                ct.ThrowIfCancellationRequested();
                await storage.CleanupSessionAsync(session.Id, ct);   // 内部已 try/catch，失败只记日志
                session.StagingPath = null;                          // 只有走到这里才认为清过
                session.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("终态会话暂存目录清理 {Count} 个（保留期 {Hours} 小时）", cleanable.Count, retentionHours);
        }

        await CleanupOrphanDirectoriesAsync(db, storage, cleanupBefore, ct);
    }

    /// <summary>
    /// 孤儿暂存目录扫描：进程崩在「建目录」和「写库」之间时留下的残骸，
    /// 数据库里根本没有对应行，靠上面那段按会话清理的逻辑永远碰不到。
    /// </summary>
    private async Task CleanupOrphanDirectoriesAsync(
        AppDbContext db, IUploadStorage storage, DateTime cleanupBefore, CancellationToken ct)
    {
        string sessionsRoot;
        try
        {
            // 根目录解析会对配置路径执行 CreateDirectory，路径不存在或没权限时抛异常。
            // 巡检不该因此整轮失败（与 SystemWatchdogWorker 的磁盘自检同一处理）。
            sessionsRoot = Path.Combine(await storage.GetStagingRootAsync(ct), "sessions");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "暂存根目录解析失败，本轮跳过孤儿目录扫描");
            return;
        }

        if (!Directory.Exists(sessionsRoot))
            return;

        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(sessionsRoot))
        {
            ct.ThrowIfCancellationRequested();

            // 目录名是 sessionId.ToString("N")。不认识的目录一律不碰——
            // 暂存根有可能被管理员指到一个还放着别的东西的盘。
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var id))
                continue;
            if (Directory.GetLastWriteTimeUtc(dir) >= cleanupBefore)
                continue;   // 可能是正在创建中的会话目录
            if (await db.UploadSessions.AnyAsync(s => s.Id == id, ct))
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "孤儿暂存目录清理失败 {Dir}", dir);
            }
        }

        if (removed > 0)
            _logger.LogInformation("孤儿暂存目录清理 {Count} 个", removed);
    }

    /// <summary>
    /// D-05：指令生命周期回收。
    ///
    /// grep "CommandStatus.Expired" 全代码库零处写入：ClaimAsync 只挑 Pending，
    /// 一旦翻成 Claimed 就再没有回到 Pending 的路径，即使 ExpiresAt 早就过了。
    /// Agent 在领取和回报之间挂掉，这条指令就永远停在 claimed 或 running——
    /// 管理端显示一条永不结束的「正在执行」，带幂等键的重发也救不了
    /// （CreateCommandAsync 只对 Failed/Cancelled/Expired/Rejected 复位）。
    ///
    /// 与 A-03 不同，这两段可以用 ExecuteUpdateAsync：不需要拿到受影响的集合，
    /// 没有任何后续动作要接在它们后面。
    /// </summary>
    private async Task RecycleStaleCommandsAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
        var now = DateTime.UtcNow;

        // 一、已过期的一律置 expired，不论处在哪一格。
        var expired = await db.Commands
            .Where(c => (c.Status == CommandStatus.Pending
                         || c.Status == CommandStatus.Claimed
                         || c.Status == CommandStatus.Running)
                        && c.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CommandStatus.Expired)
                .SetProperty(c => c.ResultCode, "COMMAND_EXPIRED")
                .SetProperty(c => c.CompletedAt, now), ct);

        // 二、领取后迟迟没上报开始的退回 pending，让 Agent 重领——
        // 这才是断线重连该有的行为：机器重启后应该继续干活，而不是等 TTL 熬完。
        //
        // Running 刻意不退回：它已经上报过开始，可能正在传几十 GB，
        // 重发会让同一份备份被传第二遍。它只能等 ExpiresAt 到期走上面那一段。
        var claimTimeout = Math.Max(60, await settings.GetIntAsync(ClaimTimeoutKey, 900, ct));
        var claimDeadline = now.AddSeconds(-claimTimeout);

        var requeued = await db.Commands
            .Where(c => c.Status == CommandStatus.Claimed
                        && c.ClaimedAt != null
                        && c.ClaimedAt < claimDeadline
                        && c.ExpiresAt >= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CommandStatus.Pending)
                .SetProperty(c => c.ClaimedAt, (DateTime?)null), ct);

        if (expired > 0 || requeued > 0)
            _logger.LogInformation("指令生命周期回收：过期 {Expired} 条，退回待领取 {Requeued} 条", expired, requeued);
    }
}
