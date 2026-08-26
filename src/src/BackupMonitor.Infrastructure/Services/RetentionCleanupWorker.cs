using System.Globalization;
using System.Text.Json;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 保留策略清理后台工作器（设计书 25 定时任务"保留策略计算"与"回收区物理删除"；第二批补充设计的落地实现）：
/// 阶段零：清扫过期恢复请求（ready 且令牌已过期、downloading 且超过宽限期、ready 且从未签发令牌超过最长等待天数 → expired），防止僵尸请求永久堆积；
/// 阶段一：回收站到期 → 物理删除仓库目录并置 deleted；
/// 阶段二：按任务绑定的策略做 GFS 保留标记（最近 N / 周 / 月 / 年），
/// 未保留且超过最短保留天数、未锁定的 available 备份集移入回收站（recycle_bin + retention_until）。
/// 锁定（backup_sets.locked 或活动 retention_locks）的备份集一律跳过；
/// 存在活动恢复请求（verifying/downloading，或已签发令牌的 ready）的备份集同样跳过，防止恢复进行中被回收。
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁），幂等（重复执行不产生额外状态变化）。
/// </summary>
public class RetentionCleanupWorker : BackgroundService
{
    /// <summary>执行间隔（分钟）system_settings 键</summary>
    public const string IntervalKey = "retention_cleanup_interval_minutes";

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:retention_cleanup";

    /// <summary>downloading 状态恢复请求的过期宽限期（小时）system_settings 键：
    /// 令牌过期后再保留该宽限期，避免误杀令牌到期前开始的慢速长下载</summary>
    public const string RestoreGraceKey = "restore_download_expire_grace_hours";

    /// <summary>ready 且从未签发令牌的恢复请求最长等待天数 system_settings 键：
    /// 验证通过后迟迟不签发令牌视为放弃，超期置 expired，避免僵尸请求无限堆积</summary>
    public const string ReadyExpireKey = "restore_ready_expire_days";
    public const string ServiceStateRetentionKey = "telemetry_service_state_retention_days";
    public const string HeartbeatRetentionKey = "telemetry_heartbeat_retention_days";

    /// <summary>锁 TTL：须大于单轮最坏耗时（批量目录物理删除），防止执行中被抢占重复执行</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionCleanupWorker> _logger;

    public RetentionCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<RetentionCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("保留策略清理工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = 60;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                intervalMinutes = Math.Max(1, await settings.GetIntAsync(IntervalKey, 60, stoppingToken));

                // 单实例锁（设计书 §25）：多实例部署时同一时刻仅一个实例执行清理
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
                // 未抢到锁说明其他实例正在执行，本轮跳过，走循环末尾的统一 Delay
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保留策略清理轮询异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>一轮清理：先清扫过期恢复请求，再算恢复保护集，最后清回收站、做 GFS 保留计算</summary>
    /// <summary>单任务单轮回收比例熔断阈值（百分比），默认 50。</summary>
    public const string RecycleBreakerPercentKey = "retention_recycle_breaker_percent";

    private async Task RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();

        var now = DateTime.UtcNow;

        // 阶段零：清扫过期恢复请求（先于保护集计算，被清扫的请求立即释放其对备份集的保护）
        var graceHours = Math.Max(1, await settings.GetIntAsync(RestoreGraceKey, 24, ct));
        var readyExpireDays = Math.Max(1, await settings.GetIntAsync(ReadyExpireKey, 7, ct));
        var serviceStateRetentionDays = Math.Max(1, await settings.GetIntAsync(ServiceStateRetentionKey, 7, ct));
        var expiredRequests = await ExpireStaleRestoreRequestsAsync(db, audit, now, TimeSpan.FromHours(graceHours), readyExpireDays, ct);

        var restoreProtectedIds = await GetRestoreProtectedSetIdsAsync(db, ct);

        // 物理删除围栏的基准。刻意复用 IUploadStorage 的解析逻辑（system_settings → 配置 → 默认路径），
        // 保证"允许删除的范围"与"实际写入的范围"同源，不会各自漂移。
        var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();
        var repositoryRoot = await storage.GetRepositoryRootAsync(ct);

        var deleted = await PurgeExpiredRecycleBinAsync(db, audit, alerting, now, restoreProtectedIds, repositoryRoot, ct);
        var recycled = await ApplyGfsRetentionAsync(db, audit, alerting, settings, now, restoreProtectedIds, ct);
        var telemetry = await CleanupTelemetryAsync(db, now, serviceStateRetentionDays, ct);

        if (deleted > 0 || recycled > 0 || expiredRequests > 0 || telemetry > 0)
            _logger.LogInformation("保留策略清理完成：恢复请求过期 {Expired}，移入回收站 {Recycled}，物理删除 {Deleted}，遥测删除 {Telemetry}",
                expiredRequests, recycled, deleted, telemetry);
    }

    /// <summary>删除高频遥测，服务状态按 5000 行分批，避免长事务锁住整张表。</summary>
    private static async Task<int> CleanupTelemetryAsync(
        AppDbContext db,
        DateTime now,
        int serviceStateRetentionDays,
        CancellationToken ct)
    {
        var cutoff = now.AddDays(-serviceStateRetentionDays);
        var deletedServiceStates = 0;
        while (true)
        {
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM client_service_states
                WHERE ctid IN (
                    SELECT ctid
                    FROM client_service_states
                    WHERE sampled_at < {cutoff}
                    LIMIT 5000)
                """, ct);
            deletedServiceStates += deleted;
            if (deleted < 5000)
                break;
        }

        var deletedNotifications = await db.AgentNotifications
            .Where(n => n.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);
        return deletedServiceStates + deletedNotifications;
    }

    /// <summary>
    /// 阶段零：清扫过期恢复请求（三类，审计以 reason 区分）。
    /// ready 且下载令牌已过期（签发后一直没人下载）→ expired，reason=token_expired；
    /// downloading 且令牌过期超过宽限期（客户端中途放弃）→ expired，reason=download_abandoned；
    /// ready 且从未签发令牌、距请求创建超过最长等待天数（视为放弃）→ expired，reason=never_issued。
    /// 逐条落审计；verifying 不在此处理（启动恢复机制会重新入队）。
    /// </summary>
    private async Task<int> ExpireStaleRestoreRequestsAsync(
        AppDbContext db,
        IAuditRecorder audit,
        DateTime now,
        TimeSpan downloadingGrace,
        int readyExpireDays,
        CancellationToken ct)
    {
        var staleReadyToken = await db.RestoreRequests
            .Where(r => r.Status == RestoreRequestStatus.Ready
                && r.DownloadExpiresAt != null && r.DownloadExpiresAt <= now)
            .ToListAsync(ct);

        var downloadingCutoff = now - downloadingGrace;
        var staleDownloading = await db.RestoreRequests
            .Where(r => r.Status == RestoreRequestStatus.Downloading
                && r.DownloadExpiresAt != null && r.DownloadExpiresAt <= downloadingCutoff)
            .ToListAsync(ct);

        var readyCutoff = now.AddDays(-readyExpireDays);
        var staleReadyNeverIssued = await db.RestoreRequests
            .Where(r => r.Status == RestoreRequestStatus.Ready
                && r.DownloadExpiresAt == null && r.RequestedAt <= readyCutoff)
            .ToListAsync(ct);

        var count = 0;
        count += await ExpireBatchAsync(db, audit, staleReadyToken, "token_expired", ct);
        count += await ExpireBatchAsync(db, audit, staleDownloading, "download_abandoned", ct);
        count += await ExpireBatchAsync(db, audit, staleReadyNeverIssued, "never_issued", ct);
        return count;
    }

    /// <summary>逐条置 expired 并落审计（含过期原因），返回处理条数</summary>
    private async Task<int> ExpireBatchAsync(
        AppDbContext db,
        IAuditRecorder audit,
        IReadOnlyList<Core.Entities.Restore.RestoreRequest> requests,
        string reason,
        CancellationToken ct)
    {
        var count = 0;
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();

            var previousStatus = request.Status;
            request.Status = RestoreRequestStatus.Expired;
            await db.SaveChangesAsync(ct);

            await audit.RecordAsync("restore.expire", AuditResult.Success, "restore_request", request.Id,
                afterData: JsonSerializer.Serialize(new
                {
                    previousStatus = EnumMapping.ToSnakeCase(previousStatus),
                    downloadExpiresAt = request.DownloadExpiresAt,
                    reason
                }), ct: ct);

            _logger.LogInformation("恢复请求 {RequestId} 已过期（原状态 {Status}，原因 {Reason}）",
                request.Id, EnumMapping.ToSnakeCase(previousStatus), reason);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 有活动恢复请求、不能被回收/物理删除的备份集：
    /// verifying/downloading 全程保护；ready 仅在已签发令牌（download_expires_at 非空）时保护——
    /// 验证通过但从未签发令牌的请求不构成进行中的下载，令牌签发后若备份集已不可用会安全失败。
    /// </summary>
    private static async Task<HashSet<Guid>> GetRestoreProtectedSetIdsAsync(AppDbContext db, CancellationToken ct)
    {
        var ids = await db.RestoreRequests
            .Where(r => r.Status == RestoreRequestStatus.Verifying
                || r.Status == RestoreRequestStatus.Downloading
                || (r.Status == RestoreRequestStatus.Ready && r.DownloadExpiresAt != null))
            .Select(r => r.BackupSetId)
            .Distinct()
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    /// <summary>阶段一：回收站到期 → 物理删除</summary>
    private async Task<int> PurgeExpiredRecycleBinAsync(
        AppDbContext db,
        IAuditRecorder audit,
        IAlertingService alerting,
        DateTime now,
        HashSet<Guid> restoreProtectedIds,
        string repositoryRoot,
        CancellationToken ct)
    {
        var expired = await db.BackupSets
            .Include(s => s.RetentionLocks)
            .Where(s => s.Status == BackupSetStatus.RecycleBin && s.RetentionUntil != null && s.RetentionUntil <= now)
            .ToListAsync(ct);

        var count = 0;
        foreach (var set in expired)
        {
            ct.ThrowIfCancellationRequested();

            if (restoreProtectedIds.Contains(set.Id))
            {
                _logger.LogInformation("备份集 {Code} 回收站到期但存在活动恢复请求，跳过物理删除", set.BackupSetCode);
                continue;
            }

            if (IsLocked(set, now))
            {
                _logger.LogInformation("备份集 {Code} 回收站到期但被锁定，跳过物理删除", set.BackupSetCode);
                continue;
            }

            try
            {
                DeleteRepositoryDirectory(set, repositoryRoot);
                set.Status = BackupSetStatus.Deleted;
                await db.SaveChangesAsync(ct);

                await audit.RecordAsync("retention.delete", AuditResult.Success, "backup_set", set.Id,
                    afterData: JsonSerializer.Serialize(new { set.BackupSetCode, set.RepositoryPath }), ct: ct);

                _logger.LogInformation("备份集 {Code} 已过保留期并物理删除：{Path}", set.BackupSetCode, set.RepositoryPath);
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "备份集 {Code} 物理删除失败", set.BackupSetCode);
                await alerting.RaiseAsync(
                    $"backup_set:{set.Id}:retention_delete_failed",
                    AlertLevel.Warning,
                    "retention_delete_failed",
                    $"备份集物理删除失败（{set.BackupSetCode}）",
                    ex.Message,
                    backupSetId: set.Id,
                    ct: ct);
            }
        }

        return count;
    }

    /// <summary>阶段二：GFS 保留计算，未保留且过最短天数的版本移入回收站</summary>
    private async Task<int> ApplyGfsRetentionAsync(
        AppDbContext db,
        IAuditRecorder audit,
        IAlertingService alerting,
        SystemSettingsProvider settings,
        DateTime now,
        HashSet<Guid> restoreProtectedIds,
        CancellationToken ct)
    {
        // 审查 P1-2 熔断：单个任务在一轮内回收的比例超过阈值时，判定为策略配置异常而非正常轮换，
        // 暂停该任务本轮回收并升级为严重告警。设错策略的典型后果就是「某任务全部备份集一次性进回收站」，
        // 这道闸把它挡在回收站之前——回收站还能捞回来，但没有理由让它先发生。
        var breakerPercent = Math.Clamp(await settings.GetIntAsync(RecycleBreakerPercentKey, 50, ct), 1, 100);
        var tasks = await db.BackupTasks
            .Include(t => t.RetentionPolicy)
            .Where(t => t.RetentionPolicyId != null)
            .ToListAsync(ct);

        var count = 0;
        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            var policy = task.RetentionPolicy;
            if (policy is null)
                continue;

            // D1：只取 Available 状态的备份集参与 GFS 保留计算，Quarantined（已隔离）天然被排除在外——
            // 隔离是人工怀疑有问题时的动作，不参与保留计算、不能用于恢复，但也不删除。
            var sets = await db.BackupSets
                .Include(s => s.RetentionLocks)
                .Where(s => s.TaskId == task.Id && s.Status == BackupSetStatus.Available)
                .OrderByDescending(s => s.UploadedAt)
                .ToListAsync(ct);

            if (sets.Count == 0)
                continue;

            var keepIds = MarkGfsRetained(sets, policy);
            var minCutoff = now.AddDays(-policy.MinimumRetentionDays);

            var candidates = new List<BackupSet>();
            foreach (var set in sets)
            {
                if (keepIds.Contains(set.Id))
                    continue;
                if (set.UploadedAt >= minCutoff)
                    continue;
                if (restoreProtectedIds.Contains(set.Id))
                {
                    _logger.LogInformation("备份集 {Code} 超出保留范围但存在活动恢复请求，跳过", set.BackupSetCode);
                    continue;
                }
                if (IsLocked(set, now))
                {
                    _logger.LogInformation("备份集 {Code} 超出保留范围但被锁定，跳过", set.BackupSetCode);
                    continue;
                }

                candidates.Add(set);
            }

            if (candidates.Count == 0)
                continue;

            // 熔断判定：本轮回收比例达到阈值即整体暂停该任务，不做部分回收——
            // 部分回收同样会删掉数据，且会让下一轮继续蚕食。
            var percent = candidates.Count * 100 / sets.Count;
            if (percent >= breakerPercent)
            {
                _logger.LogError(
                    "保留清理熔断：任务 {Task} 本轮拟回收 {N}/{Total}（{Percent}%），达到阈值 {Threshold}%，已暂停该任务回收",
                    task.Name, candidates.Count, sets.Count, percent, breakerPercent);

                await alerting.RaiseAsync(
                    $"task:{task.Id}:retention_breaker",
                    AlertLevel.Critical,
                    "retention_breaker_tripped",
                    $"保留清理熔断（任务 {task.Name}）",
                    $"策略 {policy.Name} 本轮拟将 {candidates.Count}/{sets.Count} 个备份集（{percent}%）移入回收站，" +
                    $"达到熔断阈值 {breakerPercent}%。已暂停该任务的本轮回收，请核对保留规则是否配置正确。",
                    taskId: task.Id,
                    ct: ct);
                continue;
            }

            await alerting.RecoverAsync($"task:{task.Id}:retention_breaker", ct);

            foreach (var set in candidates)
            {
                set.Status = BackupSetStatus.RecycleBin;
                set.RetentionUntil = now.AddDays(policy.RecycleBinDays);
                await db.SaveChangesAsync(ct);

                await audit.RecordAsync("retention.recycle", AuditResult.Success, "backup_set", set.Id,
                    afterData: JsonSerializer.Serialize(new
                    {
                        set.BackupSetCode,
                        policyId = policy.Id,
                        retentionUntil = set.RetentionUntil
                    }), ct: ct);

                _logger.LogInformation("备份集 {Code} 按策略 {Policy} 移入回收站，保留至 {Until:yyyy-MM-dd}",
                    set.BackupSetCode, policy.Name, set.RetentionUntil);
                count++;
            }
        }

        return count;
    }

    /// <summary>GFS 标记：最近 N 个 + 周代表 + 月代表 + 年代表（均按上传时间倒序取首个）</summary>
    private static HashSet<Guid> MarkGfsRetained(IReadOnlyList<BackupSet> setsDescending, Core.Entities.Retention.RetentionPolicy policy)
    {
        var keep = new HashSet<Guid>();

        if (policy.KeepLastCount is > 0)
        {
            foreach (var set in setsDescending.Take(policy.KeepLastCount.Value))
                keep.Add(set.Id);
        }

        MarkByPeriod(setsDescending, keep, policy.KeepWeeklyCount, s =>
        {
            var uploaded = s.UploadedAt;
            return $"{ISOWeek.GetYear(uploaded)}-W{ISOWeek.GetWeekOfYear(uploaded)}";
        });

        MarkByPeriod(setsDescending, keep, policy.KeepMonthlyCount, s => $"{s.UploadedAt:yyyy-MM}");

        MarkByPeriod(setsDescending, keep, policy.KeepYearlyCount, s => $"{s.UploadedAt:yyyy}");

        return keep;
    }

    /// <summary>按周期取代表版本：每个周期键只保留最新一个，最多 count 个周期</summary>
    private static void MarkByPeriod(
        IReadOnlyList<BackupSet> setsDescending,
        HashSet<Guid> keep,
        int? count,
        Func<BackupSet, string> periodKey)
    {
        if (count is not > 0)
            return;

        var seenPeriods = new HashSet<string>(StringComparer.Ordinal);
        var selected = 0;

        foreach (var set in setsDescending)
        {
            if (selected >= count.Value)
                break;

            var key = periodKey(set);
            if (!seenPeriods.Add(key))
                continue;

            keep.Add(set.Id);
            selected++;
        }
    }

    /// <summary>是否被锁（字段锁或活动保留锁）</summary>
    private static bool IsLocked(BackupSet set, DateTime now) =>
        set.Locked || set.RetentionLocks.Any(l => l.Active && (l.ExpiresAt == null || l.ExpiresAt > now));

    /// <summary>物理删除仓库目录（带路径安全守卫）</summary>
    private void DeleteRepositoryDirectory(BackupSet set, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(set.RepositoryPath))
        {
            _logger.LogWarning("备份集 {Code} 无仓库路径，仅做逻辑删除", set.BackupSetCode);
            return;
        }

        var full = Path.GetFullPath(set.RepositoryPath);

        // 守卫：必须是绝对路径、不能是盘符根、至少两级目录
        if (!Path.IsPathRooted(full))
            throw new InvalidOperationException($"仓库路径不是绝对路径：{full}");

        var root = Path.GetPathRoot(full);
        var relative = Path.GetRelativePath(root ?? full, full);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new InvalidOperationException($"仓库路径层级过浅，拒绝删除：{full}");

        // 围栏（最关键的一道）：待删目录必须落在当前仓库根之内。
        // backup_sets.repository_path 是数据库列，迁移出错、手工改库、仓库路径重配或上游拼接
        // bug 都可能让它指向任意目录，而这里执行的是递归删除——上面的"至少两级"守卫拦不住
        // C:\Program Files\PostgreSQL 这类路径。入库侧（UploadCommitWorker）本来就用
        // PathSafety.ResolveUnderBase 做了基目录约束，销毁侧必须对称。
        //
        // 仓库根被改过之后，历史备份集会落在围栏之外并因此无法物理删除：这是刻意选择的
        // 失败方向——拒绝并告警，交由人工确认，绝不猜测性地删除。
        if (!PathSafety.IsUnderBase(repositoryRoot, full))
            throw new InvalidOperationException(
                $"仓库目录不在当前仓库根 {Path.GetFullPath(repositoryRoot)} 之内，拒绝删除：{full}");

        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
        else
            _logger.LogWarning("备份集 {Code} 仓库目录不存在，跳过物理删除：{Path}", set.BackupSetCode, full);
    }
}
