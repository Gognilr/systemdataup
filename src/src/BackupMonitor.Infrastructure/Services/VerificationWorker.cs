using System.Threading.Channels;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 校验工作器（审计 H-18）：备份集重校验（设计书 18.6）与恢复请求下载前校验（19.3）。
///
/// 从 UploadCommitWorker 拆出来，走独立队列。原先三类工作项共享一条 SingleReader 队列，
/// 顺序处理：一次几十 GB 的入库（复制到仓库并逐文件算哈希）会把后面的备份集重校验
/// 和恢复请求校验全部堵住——表现是「点了恢复之后等了半小时还在校验」，
/// 而排队这件事在界面上完全不可见。
///
/// 恢复是有人在界面前等的交互操作，入库是后台批处理，两者不该共享一条队列。
/// </summary>
public class VerificationWorker : BackgroundService
{
    /// <summary>校验队列的 DI 键，与入库队列 <see cref="QueueKeys.Commit"/> 区分</summary>
    public const string QueueKey = QueueKeys.Verify;

    private readonly Channel<WorkItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VerificationWorker> _logger;

    public VerificationWorker(
        [FromKeyedServices(QueueKeys.Verify)] Channel<WorkItem> channel,
        IServiceScopeFactory scopeFactory,
        ILogger<VerificationWorker> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingWorkAsync(stoppingToken);

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();

                if (item.Kind == WorkKind.ReverifyBackupSet)
                    await ReverifyBackupSetAsync(db, alerting, item.Id, stoppingToken);
                else if (item.Kind == WorkKind.VerifyRestoreRequest)
                    await VerifyRestoreRequestAsync(db, alerting, item.Id, stoppingToken);
                else
                    _logger.LogWarning("校验队列收到不属于它的工作项 kind={Kind} id={Id}", item.Kind, item.Id);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "校验工作项处理失败 kind={Kind} id={Id}", item.Kind, item.Id);
            }
        }
    }

    /// <summary>重启恢复：扫描 verifying 状态的恢复请求重新入队（设计书 24.5）</summary>
    private async Task RecoverPendingWorkAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pendingRestores = await db.RestoreRequests
                .Where(r => r.Status == RestoreRequestStatus.Verifying)
                .Select(r => r.Id)
                .ToListAsync(ct);

            foreach (var id in pendingRestores)
                await _channel.Writer.WriteAsync(new WorkItem(WorkKind.VerifyRestoreRequest, id), ct);

            if (pendingRestores.Count > 0)
                _logger.LogInformation("重启恢复：{Count} 个待校验恢复请求重新入队", pendingRestores.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "校验队列重启恢复扫描失败（数据库可能尚未就绪）");
        }
    }

    private async Task ReverifyBackupSetAsync(AppDbContext db, IAlertingService alerting, Guid backupSetId, CancellationToken ct)
    {
        var backupSet = await db.BackupSets
            .Include(b => b.Files)
            .Include(b => b.Client)
            .FirstOrDefaultAsync(b => b.Id == backupSetId, ct);
        if (backupSet is null || string.IsNullOrWhiteSpace(backupSet.RepositoryPath))
            return;

        var allVerified = true;
        foreach (var file in backupSet.Files)
        {
            try
            {
                var path = Path.Combine(backupSet.RepositoryPath, file.RepositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    file.VerificationStatus = VerificationStatus.Failed;
                    allVerified = false;
                    continue;
                }

                string hash;
                await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                {
                    hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                }

                file.VerificationStatus = hash == file.Sha256.ToLowerInvariant()
                    ? VerificationStatus.Verified
                    : VerificationStatus.Failed;
                if (file.VerificationStatus == VerificationStatus.Failed)
                    allVerified = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "重校验文件失败 file={FileId}", file.Id);
                file.VerificationStatus = VerificationStatus.Failed;
                allVerified = false;
            }
        }

        backupSet.Status = allVerified ? BackupSetStatus.Available : BackupSetStatus.VerificationFailed;
        backupSet.VerifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (!allVerified)
        {
            await alerting.RaiseAsync(
                $"backup_set:{backupSet.Id}:verify_failed",
                AlertLevel.Critical,
                "verification_failed",
                $"备份 {backupSet.BackupSetCode} 重校验失败",
                "存在文件缺失或哈希不一致",
                clientId: backupSet.ClientId,
                taskId: backupSet.TaskId,
                backupSetId: backupSet.Id,
                ct: ct);
        }
        else
        {
            await alerting.RecoverAsync($"backup_set:{backupSet.Id}:verify_failed", ct);
        }
    }

    /// <summary>恢复请求下载前校验（设计书 19.3）：重算备份集全部文件哈希，只读不改动 backup_files</summary>
    private async Task VerifyRestoreRequestAsync(AppDbContext db, IAlertingService alerting, Guid requestId, CancellationToken ct)
    {
        var request = await db.RestoreRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null || request.Status is not RestoreRequestStatus.Verifying)
            return; // 已处理或状态不符，幂等跳过

        var backupSet = await db.BackupSets.AsNoTracking()
            .Include(b => b.Files)
            .FirstOrDefaultAsync(b => b.Id == request.BackupSetId, ct);

        if (backupSet is null || string.IsNullOrWhiteSpace(backupSet.RepositoryPath))
        {
            request.Status = RestoreRequestStatus.Failed;
            request.ErrorMessage = "备份仓库路径缺失，无法完成下载前校验";
            await db.SaveChangesAsync(ct);
            return;
        }

        var failedCount = 0;
        foreach (var file in backupSet.Files)
        {
            try
            {
                var path = Path.Combine(backupSet.RepositoryPath, file.RepositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    failedCount++;
                    continue;
                }

                string hash;
                await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                {
                    hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                }

                if (hash != file.Sha256.ToLowerInvariant())
                    failedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "恢复校验文件失败 file={FileId}", file.Id);
                failedCount++;
            }
        }

        if (failedCount == 0)
        {
            request.Status = RestoreRequestStatus.Ready;
            request.VerifiedAt = DateTime.UtcNow;
        }
        else
        {
            request.Status = RestoreRequestStatus.Failed;
            request.ErrorMessage = $"完整性校验失败：{failedCount} 个文件缺失或哈希不一致";
        }
        await db.SaveChangesAsync(ct);

        if (failedCount > 0)
        {
            await alerting.RaiseAsync(
                $"restore_request:{request.Id}:verify_failed",
                AlertLevel.Critical,
                "restore_verify_failed",
                $"恢复请求下载前校验失败（备份 {backupSet.BackupSetCode}）",
                request.ErrorMessage,
                clientId: backupSet.ClientId,
                taskId: backupSet.TaskId,
                backupSetId: backupSet.Id,
                ct: ct);
        }
        else
        {
            _logger.LogInformation("恢复请求 {RequestId} 校验通过，备份 {Code} 可下载", request.Id, backupSet.BackupSetCode);
        }
    }
}
