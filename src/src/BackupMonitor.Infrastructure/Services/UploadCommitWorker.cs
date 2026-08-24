using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>入库工作项类型</summary>
public enum WorkKind
{
    /// <summary>上传会话校验入库（设计书 13 校验入库 / 24 一致性）</summary>
    CommitSession,

    /// <summary>正式备份集重新校验（设计书 18.6）</summary>
    ReverifyBackupSet,

    /// <summary>恢复请求下载前完整性校验（设计书 19.3）</summary>
    VerifyRestoreRequest
}

/// <summary>入库工作项</summary>
public record WorkItem(WorkKind Kind, Guid Id);

/// <summary>
/// 校验入库后台工作器：
/// 会话完成 → 暂存目录 → 仓库临时提交目录 → 写 manifest → 原子重命名 →
/// 数据库事务写 backup_sets/backup_files → 会话标记 committed（设计书 24）。
/// 服务重启后自动扫描未完成会话恢复处理。
/// </summary>
public class UploadCommitWorker : BackgroundService
{
    private readonly Channel<WorkItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UploadCommitWorker> _logger;

    public UploadCommitWorker(
        Channel<WorkItem> channel,
        IServiceScopeFactory scopeFactory,
        ILogger<UploadCommitWorker> logger)
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
                var agentNotifications = scope.ServiceProvider.GetRequiredService<IAgentNotificationService>();
                var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();

                if (item.Kind == WorkKind.CommitSession)
                    await CommitSessionAsync(db, alerting, agentNotifications, storage, item.Id, stoppingToken);
                else if (item.Kind == WorkKind.ReverifyBackupSet)
                    await ReverifyBackupSetAsync(db, alerting, item.Id, stoppingToken);
                else
                    await VerifyRestoreRequestAsync(db, alerting, item.Id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "入库工作项处理失败 kind={Kind} id={Id}", item.Kind, item.Id);
            }
        }
    }

    /// <summary>重启恢复：扫描 verifying 状态会话重新入队（设计书 24.5）</summary>
    private async Task RecoverPendingWorkAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pendingSessions = await db.UploadSessions
                .Where(s => s.Status == UploadStatus.Verifying)
                .Select(s => s.Id)
                .ToListAsync(ct);

            foreach (var id in pendingSessions)
                await _channel.Writer.WriteAsync(new WorkItem(WorkKind.CommitSession, id), ct);

            if (pendingSessions.Count > 0)
                _logger.LogInformation("重启恢复：{Count} 个待入库会话重新入队", pendingSessions.Count);

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
            _logger.LogError(ex, "重启恢复扫描失败（数据库可能尚未就绪）");
        }
    }

    private async Task CommitSessionAsync(
        AppDbContext db,
        IAlertingService alerting,
        IAgentNotificationService agentNotifications,
        IUploadStorage storage,
        Guid sessionId,
        CancellationToken ct)
    {
        var session = await db.UploadSessions
            .Include(s => s.Files).ThenInclude(f => f.CandidateFile)
            .Include(s => s.CandidateBackupSet).ThenInclude(c => c.Task)
            .Include(s => s.CandidateBackupSet).ThenInclude(c => c.BusinessUnit)
            .Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
            return;
        if (session.Status is not UploadStatus.Verifying)
            return; // 已完成或已失败，幂等跳过

        var candidate = session.CandidateBackupSet;
        var task = candidate.Task;
        var client = session.Client;

        try
        {
            var repoRoot = await storage.GetRepositoryRootAsync(ct);

            // 正式路径完全由服务端生成（设计书 23.4）
            var versionTime = candidate.BackupBusinessTime ?? session.CompletedAt ?? session.CreatedAt;
            var versionDir = versionTime.ToString("yyyy-MM-dd_HHmmss");
            var segments = new List<string>
            {
                repoRoot,
                PathSafety.SanitizePathComponent(client.Hostname),
                PathSafety.SanitizePathComponent(task.Name)
            };
            if (candidate.BusinessUnit is not null)
                segments.Add(PathSafety.SanitizePathComponent(candidate.BusinessUnit.DisplayName));
            segments.Add(versionDir);

            var finalPath = Path.Combine(segments.ToArray());
            if (Directory.Exists(finalPath))
                finalPath = Path.Combine(Path.GetDirectoryName(finalPath)!, $"{versionDir}_{session.Id.ToString("N")[..8]}");

            var commitTemp = finalPath + $".commit-{session.Id.ToString("N")[..8]}";
            if (Directory.Exists(commitTemp))
                Directory.Delete(commitTemp, recursive: true);
            Directory.CreateDirectory(commitTemp);

            // 复制暂存文件到提交目录
            foreach (var file in session.Files)
            {
                var relativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var target = PathSafety.ResolveUnderBase(commitTemp, relativePath)
                    ?? throw new BusinessException("INVALID_REQUEST", $"非法相对路径 {file.RelativePath}", 400);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = await storage.OpenStagedFileAsync(session.Id, file.Id, ct);
                await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await source.CopyToAsync(destination, ct);
            }

            // manifest.json（架构书 12 存储目录规范）
            var uploadedAt = session.CompletedAt ?? DateTime.UtcNow;

            await using var dbScope = await db.Database.BeginTransactionAsync(ct);
            var committed = false;
            try
            {
                // 备份集记录先行创建以获得编码（状态 verifying，原子重命名成功后置 available）
                var backupSet = new BackupSet
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    TaskId = task.Id,
                    BusinessUnitId = candidate.BusinessUnitId,
                    SourceCandidateId = candidate.Id,
                    UploadSessionId = session.Id,
                    BackupSetCode = await GenerateBackupSetCodeAsync(db, ct),
                    Status = BackupSetStatus.Verifying,
                    BackupBusinessTime = candidate.BackupBusinessTime,
                    DiscoveredAt = candidate.DiscoveredAt,
                    UploadedAt = uploadedAt,
                    TotalFiles = session.Files.Count,
                    TotalBytes = session.Files.Sum(f => f.SizeBytes)
                };
                db.BackupSets.Add(backupSet);
                await db.SaveChangesAsync(ct);

                var manifest = new
                {
                    backupSetId = backupSet.BackupSetCode,
                    clientId = client.Hostname,
                    taskName = task.Name,
                    businessUnit = candidate.BusinessUnit?.DisplayName,
                    sourcePath = candidate.SourceRoot,
                    discoveredAt = candidate.DiscoveredAt,
                    uploadedAt,
                    verifiedAt = DateTime.UtcNow,
                    fileCount = session.Files.Count,
                    totalBytes = session.Files.Sum(f => f.SizeBytes),
                    files = session.Files
                        .OrderBy(f => f.RelativePath)
                        .Select(f => new
                        {
                            path = f.RelativePath,
                            size = f.SizeBytes,
                            lastModified = f.CandidateFile?.LastModifiedAt ?? DateTime.UtcNow,
                            // P2-8：与 BackupFile.Sha256 同源——服务端实测值优先，
                            // 恢复前校验用的就是这个值，清单必须和它一致
                            sha256 = f.ServerSha256 ?? f.ExpectedSha256
                        })
                        .ToList()
                };
                var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                // 审查 P1-6：清单是恢复时的权威依据，必须先于目录改名真正落盘。
                // 原先以 FileAccess.Read 打开再 Flush(flushToDisk:true) 是空操作——
                // 对只读流刷盘一个字节都不会同步，断电后可能出现"正式目录已存在、清单为空"。
                var manifestPath = Path.Combine(commitTemp, "manifest.json");
                var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
                await using (var mf = new FileStream(
                    manifestPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, FileOptions.WriteThrough))
                {
                    await mf.WriteAsync(manifestBytes, ct);
                    mf.Flush(flushToDisk: true);
                }

                // 原子重命名为正式目录
                Directory.Move(commitTemp, finalPath);

                var finalManifestPath = Path.Combine(finalPath, "manifest.json");
                var manifestHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(finalManifestPath, ct))).ToLowerInvariant();

                backupSet.Status = BackupSetStatus.Available;
                backupSet.VerifiedAt = DateTime.UtcNow;
                backupSet.RepositoryPath = finalPath;
                backupSet.ManifestPath = finalManifestPath;
                backupSet.ManifestSha256 = manifestHash;

                foreach (var file in session.Files)
                {
                    db.BackupFiles.Add(new BackupFile
                    {
                        Id = Guid.NewGuid(),
                        BackupSetId = backupSet.Id,
                        RelativePath = file.RelativePath,
                        FileName = Path.GetFileName(file.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                        SizeBytes = file.SizeBytes,
                        // P2-9：源文件修改时间是备份的实质元数据，不能用入库时刻顶替
                        LastModifiedAt = file.CandidateFile?.LastModifiedAt ?? DateTime.UtcNow,
                        Sha256 = file.ServerSha256 ?? file.ExpectedSha256,
                        RepositoryRelativePath = file.RelativePath,
                        VerificationStatus = VerificationStatus.Verified
                    });
                }

                session.Status = UploadStatus.Committed;
                session.VerifiedAt = DateTime.UtcNow;
                session.CommittedAt = DateTime.UtcNow;
                session.UpdatedAt = DateTime.UtcNow;

                task.LastSuccessAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);
                await dbScope.CommitAsync(ct);
                committed = true;

                // ── 以下全部是提交之后的收尾动作：备份集已落库、正式目录已就位。
                // 任何一步都不得把异常抛给下面的 catch —— 那里会回滚事务并删除正式目录，
                // 等于把一次已经成功的备份毁掉。因此逐个兜住，失败只记录。
                try
                {
                    await storage.CleanupSessionAsync(session.Id, ct);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(
                        cleanupEx, "暂存目录清理失败 session={SessionId}（备份已入库，不影响结果）", session.Id);
                }

                try
                {
                    await alerting.RecoverAsync($"task:{task.Id}:upload_failed", ct);
                }
                catch (Exception recoverEx)
                {
                    _logger.LogWarning(
                        recoverEx, "上传失败告警恢复失败 task={TaskId}（备份已入库，不影响结果）", task.Id);
                }

                // 备份已完成入库后向对应客户端排队一条一次性托盘提示。
                try
                {
                    await agentNotifications.EnqueueAsync(
                        client.Id,
                        "backup_completed",
                        "info",
                        "备份完成",
                        $"任务 {task.Name}：备份集 {backupSet.BackupSetCode}，{backupSet.TotalFiles} 个文件，{backupSet.TotalBytes} 字节",
                        $"backup_set:{backupSet.Id}:completed",
                        backupSetId: backupSet.Id,
                        ct: ct);
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception notificationEx)
                {
                    // 提示队列异常不能回滚已经成功的备份。
                    _logger.LogWarning(notificationEx, "备份完成提示入队失败 backupSet={BackupSetId}", backupSet.Id);
                }

                _logger.LogInformation(
                    "会话 {SessionId} 入库成功 backupSet={Code} path={Path}",
                    session.Id, backupSet.BackupSetCode, finalPath);
            }
            catch
            {
                // 提交之后本不该再走到这里（收尾动作已各自兜住异常）。万一走到了，
                // 绝不能回滚或删目录：事务已提交、正式目录已就位，那样做就是销毁一次成功的备份。
                // 之所以留这道闸，是因为原先的写法只靠「对已提交事务调 RollbackAsync 会先抛异常」
                // 这个实现细节侥幸躲开删除，换个 EF/Npgsql 版本就可能真的删下去。
                if (committed)
                    throw;

                await dbScope.RollbackAsync(ct);

                // 数据库写入失败不得暴露半成品正式目录（设计书 24.3）
                try
                {
                    if (Directory.Exists(finalPath) && !Directory.Exists(commitTemp))
                        Directory.Move(finalPath, commitTemp);
                    if (Directory.Exists(commitTemp))
                        Directory.Delete(commitTemp, recursive: true);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogError(cleanupEx, "回滚清理提交目录失败 {Path}", commitTemp);
                }

                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会话 {SessionId} 校验入库失败", sessionId);

            session.Status = UploadStatus.Failed;
            session.ErrorCode = "COMMIT_FAILED";
            session.ErrorMessage = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await alerting.RaiseAsync(
                $"session:{session.Id}:commit_failed",
                AlertLevel.Critical,
                "upload_commit_failed",
                $"上传会话入库失败（任务 {task.Name}）",
                ex.Message,
                clientId: client.Id,
                taskId: task.Id,
                ct: ct);
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

    /// <summary>生成备份集编码 BS-yyyy-NNNN（唯一约束冲突时重试）</summary>
    /// <summary>
    /// 生成备份集编码 BS-yyyy-NNNN（审查 P2-7）。
    ///
    /// 序号取自数据库序列 backup_set_code_seq（V011）：单调递增、不随删除回落，
    /// 并发下由数据库保证唯一。原先的 COUNT(*)+1 会在保留策略删除历史备份集后
    /// 让计数回落，使新备份集复用已注销的编码，破坏 manifest/审计/告警的可追溯性。
    /// </summary>
    private static async Task<string> GenerateBackupSetCodeAsync(AppDbContext db, CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT nextval('backup_set_code_seq')";

        var shouldClose = command.Connection!.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await command.Connection.OpenAsync(ct);
        else
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        try
        {
            var next = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            return $"BS-{year}-{next:0000}";
        }
        finally
        {
            if (shouldClose)
                await command.Connection.CloseAsync();
        }
    }
}
