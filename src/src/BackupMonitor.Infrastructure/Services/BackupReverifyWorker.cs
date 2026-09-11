using System.Threading.Channels;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 备份完整性的定期复查（D8）。
///
/// 校验失败的告警文案一直写着「备份 xxx 定期复查没通过」——而系统里根本没有任何东西
/// 在定期复查：WorkKind.ReverifyBackupSet 唯一的入队点是备份详情页上那个手工按钮。
/// 承诺了却没做的事比没承诺更糟：人会以为备份的完整性一直有人在看着，
/// 于是永远不会自己去抽查，而磁盘静默损坏、误删、被勒索软件加密这几种情况
/// 恰恰只有复查才发现得了——恢复的时候才发现，就太晚了。
///
/// 做法刻意保守：按 verified_at 最旧优先，每轮只取固定份数入队，间隔按小时算。
/// 复查要整份重算哈希，是纯磁盘读，与上传抢的是同一块盘——
/// 一次把全部备份排进去会让当晚的备份全部变慢，那是拿一个问题换另一个问题。
///
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁），幂等
/// （已经在校验中的不会被重复入队）。
/// </summary>
public class BackupReverifyWorker : BackgroundService
{
    /// <summary>
    /// 显式的启用 / 停用开关（V040 · R18）。
    ///
    /// 原来没有关闭的办法：clamp 是 1~720 小时，现场真要临时停掉它
    /// （比如正在做一次全量迁移，盘已经满负荷），只能把间隔改成 720 小时——
    /// 那是「关掉」的一个变相写法，而不是关掉，而且没人看得出它被关过。
    /// </summary>
    public const string EnabledKey = "backup_reverify_enabled";

    /// <summary>复查间隔（小时）system_settings 键（V040 起默认 168 = 7 天）</summary>
    public const string IntervalHoursKey = "backup_reverify_interval_hours";

    /// <summary>每轮入队的份数 system_settings 键（V040 起默认 1）</summary>
    public const string BatchSizeKey = "backup_reverify_batch_size";

    /// <summary>默认复查间隔（小时）。7 天：复查是纯磁盘读，与上传抢同一块盘。</summary>
    public const int DefaultIntervalHours = 168;

    /// <summary>默认每轮份数。</summary>
    public const int DefaultBatchSize = 1;

    /// <summary>单实例锁键（设计书 §25，scheduled_locks.lock_key）</summary>
    public const string LockKey = "worker:backup_reverify";

    /// <summary>锁 TTL：这里只做入队，一轮很快</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 一份备份多久没复查过才轮得到它。取复查间隔的整数倍没有意义——
    /// 真正的判据是「按 verified_at 最旧优先」，这个下限只是避免刚入库的那一批
    /// 立刻被再算一遍哈希（入库时已经全量校验过一次了）。
    /// </summary>
    private static readonly TimeSpan MinAgeBeforeReverify = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<WorkItem> _workChannel;
    private readonly ILogger<BackupReverifyWorker> _logger;

    public BackupReverifyWorker(
        IServiceScopeFactory scopeFactory,
        [FromKeyedServices(QueueKeys.Verify)] Channel<WorkItem> workChannel,
        ILogger<BackupReverifyWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _workChannel = workChannel;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("备份定期复查工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalHours = DefaultIntervalHours;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                intervalHours = Math.Clamp(
                    await settings.GetIntAsync(IntervalHoursKey, DefaultIntervalHours, stoppingToken), 1, 720);

                // 关掉之后**一个工作项都不产生**（R18）。仍然按间隔醒来重读配置，
                // 这样重新打开不必等重启服务端——现场把它关掉往往是临时的。
                if (!await settings.GetBoolAsync(EnabledKey, true, stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
                    continue;
                }

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
                // 未抢到锁说明其他实例正在入队，本轮跳过
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "备份定期复查轮询异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 一轮入队。公开而不是私有，是为了让集成测试能确定性地驱动一轮——
    /// 靠 StartAsync 等首轮跑完再轮询断言，测试会变成对时序的赌博。
    /// </summary>
    public async Task<int> RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();

        // 关掉时这里也要挡一道：RunPassAsync 是公开的（测试与手工触发都走它），
        // 只在循环里判断的话，关掉之后仍然有别的入口能把工作项排进去。
        if (!await settings.GetBoolAsync(EnabledKey, true, ct))
            return 0;

        var batchSize = Math.Clamp(await settings.GetIntAsync(BatchSizeKey, DefaultBatchSize, ct), 1, 200);
        var cutoff = DateTime.UtcNow - MinAgeBeforeReverify;

        // 只复查「还活着而且真的能复查」的那些：
        //   - recycle_bin / deleted：已经删了，文件可能都不在了；
        //   - quarantined：人工判断先别动它，复查也不该改它的状态；
        //   - verifying_since 非空：已经在队列里排着了，别放第二次；
        //   - 没有仓库路径：没有东西可以校验，那是另一类问题（对帐工作器管）。
        var candidates = await db.BackupSets.AsNoTracking()
            .Where(s => s.Status != BackupSetStatus.RecycleBin
                && s.Status != BackupSetStatus.Deleted
                && s.Status != BackupSetStatus.Quarantined
                && s.VerifyingSince == null
                && s.RepositoryPath != null
                && (s.VerifiedAt == null || s.VerifiedAt < cutoff))
            // 最旧优先：复查的价值在于「最久没人看过的那一份」，
            // 而按 id 或随机取会让某些备份长期轮不到。
            .OrderBy(s => s.VerifiedAt ?? s.UploadedAt)
            .Take(batchSize)
            .Select(s => new { s.Id, s.BackupSetCode })
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return 0;

        // 先打上「校验中」再入队：入队之后到工作器取走之间有一段窗口，
        // 这段时间里没有标记的话，下一轮巡检会把同一份再排一次。
        var ids = candidates.Select(c => c.Id).ToList();
        var now = DateTime.UtcNow;
        await db.BackupSets
            .Where(s => ids.Contains(s.Id) && s.VerifyingSince == null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.VerifyingSince, now), ct);

        foreach (var candidate in candidates)
            await _workChannel.Writer.WriteAsync(new WorkItem(WorkKind.ReverifyBackupSet, candidate.Id), ct);

        _logger.LogInformation("备份定期复查：本轮入队 {Count} 份（{Codes}）",
            candidates.Count, string.Join("、", candidates.Select(c => c.BackupSetCode)));

        return candidates.Count;
    }
}
