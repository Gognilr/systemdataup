using BackupMonitor.Shared.Security;
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
/// 两条后台队列的 DI 键（审计 H-18）。
///
/// 原先三类工作项共享一条队列顺序处理，一次几十 GB 的入库会把后面的
/// 备份集重校验和恢复请求校验全部堵住——「点了恢复之后等了半小时还在校验」，
/// 而排队这件事在界面上完全不可见。恢复是有人在界面前等的交互操作，
/// 入库是后台批处理，两者不该共享一条队列。
/// </summary>
public static class QueueKeys
{
    /// <summary>上传会话校验入库队列，由 <see cref="UploadCommitWorker"/> 消费</summary>
    public const string Commit = "commit";

    /// <summary>备份集重校验 / 恢复请求校验队列，由 <see cref="VerificationWorker"/> 消费</summary>
    public const string Verify = "verify";
}

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
        [FromKeyedServices(QueueKeys.Commit)] Channel<WorkItem> channel,
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

                // 审计 H-18：这条队列现在只走入库。重校验与恢复校验拆去了
                // VerificationWorker，不再排在几十 GB 的入库后面。
                if (item.Kind == WorkKind.CommitSession)
                    await CommitSessionAsync(db, alerting, agentNotifications, storage, item.Id, stoppingToken);
                else
                    _logger.LogWarning("入库队列收到不属于它的工作项 kind={Kind} id={Id}", item.Kind, item.Id);
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

            // 待校验的恢复请求由 VerificationWorker 自己扫描重入队（审计 H-18）。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重启恢复扫描失败（数据库可能尚未就绪）");
        }
    }

    /// <summary>
    /// 会话校验入库（设计书 13 / 24）。
    ///
    /// 公开而不是私有，是为了让集成测试能确定性地驱动一次入库——
    /// 靠 Channel 投递再等 BackgroundService 消费，测试会变成对时序的赌博，
    /// 而这条路径上「失败时怎么收场」恰恰是最需要被盯住的部分（审计 E-07）。
    /// </summary>
    public async Task CommitSessionAsync(
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
            // ── 整文件哈希复核（实施方案 T2）────────────────────────────────────
            //
            // 这一步原先在 CompleteFileAsync 里，也就是**客户端同步等待的请求路径上**：
            // 6GB 的备份在慢盘上要整读几分钟，而客户端的 HTTP 超时是 10 分钟——
            // 正常上传会以超时告终，报出来的错还跟哈希毫无关系。挪到这里之后，
            // 落盘保证与判定口径一个字没变，变的只是「谁在等」。
            //
            // 位置刻意放在**创建任何仓库目录之前**：哈希不符时直接抛出去，
            // 此刻还没有 commitTemp、没有硬链接、没有半份复制，外层 catch 不需要清理任何东西。
            // 跨卷复制那条路径因此会多读一遍（复制本可以边读边算），但那是罕见分支，
            // 而「失败路径不留垃圾」比省掉一次顺序读重要得多。
            foreach (var file in session.Files)
            {
                ct.ThrowIfCancellationRequested();
                var serverSha = await storage.ComputeFileHashAsync(session.Id, file.Id, ct);

                // 审查 P1-1 的判定原样保留：以预检清单的 expected_sha256 为权威基准
                // （服务端持有），客户端在 complete 时声明的值只作附加交叉验证——
                // 它由上传方单方面给出，不能拿它当唯一判据。
                var mismatchReason =
                    !string.Equals(serverSha, file.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
                        ? $"实测哈希与预检期望值不一致（期望 {file.ExpectedSha256}，实测 {serverSha}）"
                    : !string.IsNullOrWhiteSpace(file.ClientDeclaredSha256)
                      && !string.Equals(serverSha, file.ClientDeclaredSha256, StringComparison.OrdinalIgnoreCase)
                        ? $"客户端声明哈希与实测不一致（声明 {file.ClientDeclaredSha256}，实测 {serverSha}）"
                    : null;

                file.ServerSha256 = serverSha;
                if (mismatchReason is not null)
                {
                    file.Status = UploadFileStatus.Failed;
                    file.ErrorCode = "FILE_HASH_MISMATCH";
                    await db.SaveChangesAsync(ct);
                    throw new FileHashMismatchException(file.RelativePath, mismatchReason);
                }

                file.Status = UploadFileStatus.Verified;
            }

            // 复核通过才累加 verified_bytes（实施方案 T3）。
            //
            // 原先这一句在 CompleteFileAsync 里，写法是「把本会话已 verified 的所有文件
            // SUM 一遍」——正是 RecordReceivedChunkAsync 的注释里论证并改掉的那个反模式，
            // 只是从块级搬到了文件级：N 个文件要扫 N(N+1)/2 行，5000 个文件约 1250 万行，
            // 全为算一个本可以直接加出来的数。
            session.VerifiedBytes = session.Files.Sum(f => f.SizeBytes);

            var repoRoot = await storage.GetRepositoryRootAsync(ct);

            // 正式路径完全由服务端生成（设计书 23.4）
            var versionTime = candidate.BackupBusinessTime ?? session.CompletedAt ?? session.CreatedAt;
            var versionDir = versionTime.ToString("yyyy-MM-dd_HHmmss");
            // 客户端这一层用管理员起的名字，不是 Windows 主机名。
            // 仓库里一排 WIN-9INJ5VMRLSK\、WIN-LHF1SSRHPQN\，人打开文件夹认不出哪个是哪台机器,
            // 而「这份备份是哪台服务器的」正是这一层目录唯一要回答的问题。
            //
            // 重名时补上主机名：display_name 没有唯一约束，两台机器起同一个名字
            // 会把各自的备份混进同一个目录——那比名字难看严重得多。
            // 已入库的版本按 backup_sets.repository_path 定位（RepositoryReconcileWorker
            // 读的就是它），所以改名不会让旧版本失联；改名之后的新版本落进新目录。
            var clientFolder = client.DisplayName;
            if (string.IsNullOrWhiteSpace(clientFolder))
                clientFolder = client.Hostname;
            else if (await db.Clients.AnyAsync(c => c.Id != client.Id && c.DisplayName == client.DisplayName, ct))
                clientFolder = $"{clientFolder}_{client.Hostname}";

            var segments = new List<string>
            {
                repoRoot,
                PathSafety.SanitizePathComponent(clientFolder),
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

            // 暂存文件进仓库：同卷 NTFS 建硬链接，否则复制
            var linkedFiles = 0;
            var copiedFiles = 0;
            foreach (var file in session.Files)
            {
                var relativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var target = PathSafety.ResolveUnderBase(commitTemp, relativePath)
                    ?? throw new BusinessException("INVALID_REQUEST", $"非法相对路径 {file.RelativePath}", 400);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // 同卷 NTFS 时用硬链接代替整份复制：6GB 备份的入库耗时从「6GB 读 + 6GB 写」
                // 降到一次元数据操作。暂存文件仍然在（链接计数为 2），提交失败还能重试——
                // 这正是它优于 File.Move 的地方。跨卷或非 NTFS 时 TryCreate 返回 false，
                // 落到下面原来的复制路径，行为与之前完全一致。
                var stagedPath = await storage.GetStagedFilePathAsync(session.Id, file.Id, ct);
                if (HardLink.TryCreate(stagedPath, target))
                {
                    linkedFiles++;
                    continue;
                }

                await using var source = await storage.OpenStagedFileAsync(session.Id, file.Id, ct);
                // 入库是把整份暂存文件顺序复制进仓库（6GB 备份 = 6GB 读 + 6GB 写）。
                // 80KB 默认缓冲对这个量级太小，1MB + SequentialScan 让两端的预读/回写
                // 都能成批下发。源流的缓冲在 OpenStagedFileAsync 里一并调过了。
                await using var destination = new FileStream(
                    target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, 1024 * 1024, ct);
                copiedFiles++;
            }

            if (linkedFiles > 0)
                _logger.LogInformation(
                    "会话 {SessionId} 入库：{Linked} 个文件走硬链接（同卷），{Copied} 个文件复制",
                    session.Id, linkedFiles, copiedFiles);

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
                    TotalBytes = session.Files.Sum(f => f.SizeBytes),
                    // 「大小可疑」跟着备份集走（R15）。候选在入库之后就不再是使用者会打开的东西，
                    // 而人是在备份列表和详情页上看这件事的——标记留在候选上等于没有标记。
                    SizeSuspicious = candidate.SizeSuspicious,
                    SizeSuspicionReason = candidate.SizeSuspicionReason
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
                    // 恢复的键要与 CommandService 触发时用的那一份一模一样，否则告警永远挂着。
                    // 那边有候选就按候选、没有就退到任务，这里必须照同一个口径，
                    // 两个都恢复一次：这次入库既证明了「这个账套好了」，
                    // 也证明了「这个任务好了」——后者是候选出现之前那些失败留下的键。
                    await alerting.RecoverAsync(
                        $"candidate:{session.CandidateBackupSetId}:upload_failed", ct);
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
            catch (Exception inner)
            {
                // 提交之后本不该再走到这里（收尾动作已各自兜住异常）。万一走到了，
                // 绝不能回滚或删目录：事务已提交、正式目录已就位，那样做就是销毁一次成功的备份。
                // 之所以留这道闸，是因为原先的写法只靠「对已提交事务调 RollbackAsync 会先抛异常」
                // 这个实现细节侥幸躲开删除，换个 EF/Npgsql 版本就可能真的删下去。
                //
                // 审计 E-07：抛一个可辨认的类型而不是裸 throw。裸 throw 会被外层 catch
                // 无条件接住后置 Failed + 发 Critical 告警——备份实际是 Available、
                // 正式目录已就位、文件都在，会话却被记成失败。更麻烦的是
                // CommandService 的 uploadAcceptedButCommitFailed 分支专门找
                // 「该候选存在 Failed 会话」，于是会把已经成功的上传指令复位重发，
                // 客户端把几十 GB 再传一遍。
                if (committed)
                    throw new PostCommitException(inner);

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
        catch (PostCommitException ex)
        {
            // 审计 E-07：备份是成功的——事务已提交、正式目录已就位、备份集是 Available。
            // 坏掉的只是提交之后的某个收尾动作。绝不改会话状态、绝不告警：
            // 那会让一次成功的备份在管理端表现为「失败 + 严重告警」，
            // 并触发上传指令复位重发。
            _logger.LogError(ex.InnerException ?? ex,
                "会话 {SessionId} 入库已提交，但提交后的收尾动作失败（备份有效，无需重传）", sessionId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 审计 E-07：停机不是失败。会话留在 verifying，
            // RecoverPendingWorkAsync 会在重启后把它重新入队。
            _logger.LogInformation("会话 {SessionId} 入库因服务停止中断，重启后将重新入队", sessionId);
            throw;
        }
        catch (FileHashMismatchException ex)
        {
            // 实施方案 T2：哈希不符和「入库过程出错」是两回事，必须给不同的错误码。
            // COMMIT_FAILED 的含义是「传上来的东西是对的，存进仓库时出了问题」——
            // 那种情况重传没用，该查的是服务端磁盘；而 FILE_HASH_MISMATCH 说的是
            // 「传上来的东西就不对」，正确处置是重传。两者混成一个码，
            // 现场只能靠猜。走到这里时还没有创建任何仓库目录，没有东西需要清理。
            _logger.LogError(ex, "会话 {SessionId} 整文件复核未通过", sessionId);

            session.Status = UploadStatus.Failed;
            session.ErrorCode = "FILE_HASH_MISMATCH";
            session.ErrorMessage = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            await alerting.RaiseAsync(
                $"session:{session.Id}:commit_failed",
                AlertLevel.Critical,
                "upload_commit_failed",
                $"备份传完了，但内容和预检时对不上（任务 {task.Name}）",
                ex.Message,
                clientId: client.Id,
                taskId: task.Id,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会话 {SessionId} 校验入库失败", sessionId);

            session.Status = UploadStatus.Failed;
            session.ErrorCode = "COMMIT_FAILED";
            session.ErrorMessage = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            session.UpdatedAt = DateTime.UtcNow;

            // 收尾用 CancellationToken.None：走到这里时 ct 可能已经取消
            // （比如失败原因本身就是停机之外的取消），那样这段收尾自己也会抛，
            // 会话就永远停在 verifying 而没有任何记录说明为什么。
            await db.SaveChangesAsync(CancellationToken.None);

            await alerting.RaiseAsync(
                $"session:{session.Id}:commit_failed",
                AlertLevel.Critical,
                "upload_commit_failed",
                $"备份已传完，但存入备份库时失败（任务 {task.Name}）",
                ex.Message,
                clientId: client.Id,
                taskId: task.Id,
                ct: CancellationToken.None);
        }
    }

    /// <summary>
    /// 提交之后的收尾动作失败（审计 E-07）。
    /// 它存在的唯一目的是让外层 catch 能把「备份成功但收尾出错」
    /// 和「备份真的失败了」区分开——两者的正确处置完全相反。
    /// </summary>
    private sealed class PostCommitException : Exception
    {
        public PostCommitException(Exception inner)
            : base("入库事务已提交，提交后的收尾动作失败", inner)
        {
        }
    }

    /// <summary>
    /// 整文件哈希复核未通过（实施方案 T2）。
    /// 单独一个类型，是为了让外层 catch 能把它和「入库过程出错」分开：
    /// 前者要重传，后者重传没用。抛出时尚未创建任何仓库目录。
    /// </summary>
    private sealed class FileHashMismatchException : Exception
    {
        public FileHashMismatchException(string relativePath, string reason)
            : base($"文件哈希错误（{relativePath}）：{reason}")
        {
        }
    }

    /// <summary>
    /// 生成备份集编码 BS-NNNNNN（审查 P2-7 · 审计 E-16）。
    ///
    /// 序号取自数据库序列 backup_set_code_seq（V011）：单调递增、不随删除回落，
    /// 并发下由数据库保证唯一。原先的 COUNT(*)+1 会在保留策略删除历史备份集后
    /// 让计数回落，使新备份集复用已注销的编码，破坏 manifest/审计/告警的可追溯性。
    ///
    /// E-16：格式里原本有年份（BS-yyyy-NNNN），但序列是全局的、跨年不重置，
    /// 于是「2026 年的第 8931 个」实际是「有史以来的第 8931 个，前面贴了个年份」，
    /// 跨年那一刻直接从 BS-2026-8931 变成 BS-2027-8932，四位补零也早溢出成五位。
    /// 与其做按年重置（需要复合唯一约束加重试，跨年那一刻仍有并发窗口，
    /// 而这只是一个展示属性），不如承认它就是全局流水号，把会误导人的年份去掉。
    /// </summary>
    private static async Task<string> GenerateBackupSetCodeAsync(AppDbContext db, CancellationToken ct)
    {
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
            return $"BS-{next:000000}";
        }
        finally
        {
            if (shouldClose)
                await command.Connection.CloseAsync();
        }
    }
}
