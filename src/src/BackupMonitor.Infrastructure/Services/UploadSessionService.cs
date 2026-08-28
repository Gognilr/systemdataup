using BackupMonitor.Core.Entities.Upload;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>上传会话服务（设计书 14 上传协议）</summary>
public interface IUploadSessionService
{
    Task<CreateUploadSessionResponse> CreateSessionAsync(Guid clientId, CreateUploadSessionRequest request, CancellationToken ct = default);
    Task<MissingChunksResponse> GetMissingChunksAsync(Guid clientId, Guid sessionId, Guid fileId, CancellationToken ct = default);
    Task<UploadChunkResponse> UploadChunkAsync(Guid clientId, Guid sessionId, Guid fileId, int chunkIndex, long offset, string expectedHash, Stream data, CancellationToken ct = default);
    Task<CompleteUploadFileResponse> CompleteFileAsync(Guid clientId, Guid sessionId, Guid fileId, CompleteUploadFileRequest request, CancellationToken ct = default);
    Task<CompleteUploadSessionResponse> CompleteSessionAsync(Guid clientId, Guid sessionId, CompleteUploadSessionRequest request, CancellationToken ct = default);
    Task<UploadSessionStatusResponse> GetSessionStatusAsync(Guid clientId, Guid sessionId, CancellationToken ct = default);
    Task CancelSessionAsync(Guid clientId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// 这次传挂了，但别把会话作废（待办方案 E）。
    ///
    /// Agent 原先遇到异常一律调 cancel，会话置 cancelled、暂存随后被清，
    /// 下次只能从 0 开始——也就是「断网能续传，出错不能续传」。
    /// 这里把会话停在 retry_wait：状态仍然可写、暂存仍然在，
    /// 同一候选的下一条上传指令凭幂等键拿回这个会话，从缺哪块传哪块继续。
    /// </summary>
    Task InterruptSessionAsync(Guid clientId, Guid sessionId, InterruptUploadSessionRequest request, CancellationToken ct = default);
}

/// <summary>上传会话实现</summary>
public class UploadSessionService : IUploadSessionService
{
    /// <summary>
    /// 管理端暂停了这次传输时返回的错误码。
    ///
    /// 与泛化的 UPLOAD_SESSION_CONFLICT 分开是必需的，不是为了错误信息好看：
    /// Agent 要据此把「管理员按了暂停」和「会话状态异常」区别对待——前者应当安静地
    /// 停下并等待恢复指令，后者才是需要重试或告警的故障。两者共用一个码时，
    /// Agent 只能一律当成可重试错误，于是暂停变成「退避几秒后接着传」。
    /// </summary>
    public const string PausedErrorCode = "UPLOAD_SESSION_PAUSED";

    private static readonly UploadStatus[] WritableStatuses = UploadSessionStatuses.Writable;
    private static readonly UploadStatus[] ActiveStatuses = UploadSessionStatuses.Active;

    private readonly AppDbContext _db;
    private readonly IUploadStorage _storage;
    private readonly SystemSettingsProvider _settings;
    private readonly IAuditRecorder _audit;
    private readonly System.Threading.Channels.Channel<WorkItem> _commitQueue;
    private readonly ILogger<UploadSessionService> _logger;

    public UploadSessionService(
        AppDbContext db,
        IUploadStorage storage,
        SystemSettingsProvider settings,
        IAuditRecorder audit,
        [Microsoft.Extensions.DependencyInjection.FromKeyedServices(QueueKeys.Commit)]
        System.Threading.Channels.Channel<WorkItem> commitQueue,
        ILogger<UploadSessionService> logger)
    {
        _db = db;
        _storage = storage;
        _settings = settings;
        _audit = audit;
        _commitQueue = commitQueue;
        _logger = logger;
    }

    public async Task<CreateUploadSessionResponse> CreateSessionAsync(Guid clientId, CreateUploadSessionRequest request, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new BusinessException("CLIENT_NOT_REGISTERED", "客户端未注册", 401);
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CLIENT_DISABLED", "客户端已禁用或已注销", 403);

        // 幂等：同键返回原会话（设计书 8.5）
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await _db.UploadSessions
                .Include(s => s.Files)
                .FirstOrDefaultAsync(s => s.IdempotencyKey == request.IdempotencyKey && s.ClientId == clientId, ct);
            if (existing is not null)
            {
                // 暂停中的会话不交回去。自动模式下预检每跑一轮就会用
                // auto-upload:{candidateId} 重下一条上传指令，幂等键命中的正是这一条；
                // 照常返回「可续传」等于绕过管理员的暂停，把会话又塞回 Agent 手里。
                if (existing.Status == UploadStatus.Paused)
                    throw new BusinessException(PausedErrorCode, "这次传输已被管理员暂停，恢复后会从断点继续", 409);

                if (existing.Status is not (UploadStatus.Failed or UploadStatus.Cancelled or UploadStatus.Expired))
                {
                    var existingTimeout = await _settings.GetIntAsync(LifecycleExpiryWorker.SessionTimeoutKey, LifecycleExpiryWorker.DefaultSessionTimeoutSeconds, ct);
                    return BuildCreateResponse(existing, resumed: true, existingTimeout);
                }

                // 失败会话保留作审计，但释放幂等键，让同一候选可以创建全新的重试会话。
                existing.IdempotencyKey = null;
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }

        var candidate = await _db.Set<Core.Entities.Backup.CandidateBackupSet>()
            .Include(c => c.Task)
            .Include(c => c.Files)
            .FirstOrDefaultAsync(c => c.Id == request.CandidateBackupSetId, ct)
            ?? throw new NotFoundException("候选备份集", request.CandidateBackupSetId);

        // 任务归属 + 候选归属
        if (candidate.ClientId != clientId || candidate.Task.ClientId != clientId)
            throw new BusinessException("FORBIDDEN", "候选备份集不属于当前客户端", 403);

        // 候选有效性
        if (candidate.PrecheckStatus != PrecheckStatus.Passed)
            throw new BusinessException("UPLOAD_NOT_PERMITTED", "候选尚未通过预检，不允许上传", 409);
        if (candidate.ExpiresAt is not null && candidate.ExpiresAt <= DateTime.UtcNow)
            throw new BusinessException("CANDIDATE_EXPIRED", "候选已过期", 409);
        if (candidate.SupersededById is not null)
            throw new BusinessException("CANDIDATE_CHANGED", "候选已被新候选替代", 409);

        // 是否已入库（一个候选只能产生一个正式版本）
        if (await _db.BackupSets.AnyAsync(b => b.SourceCandidateId == candidate.Id, ct))
            throw new BusinessException("CANDIDATE_ALREADY_ARCHIVED", "该候选已正式入库", 409);
        if (await _db.UploadSessions.AnyAsync(s => s.CandidateBackupSetId == candidate.Id && s.Status == UploadStatus.Committed, ct))
            throw new BusinessException("CANDIDATE_ALREADY_ARCHIVED", "该候选已正式入库", 409);

        // manifest 一致性
        if (!string.IsNullOrWhiteSpace(request.ManifestHash)
            && !string.IsNullOrWhiteSpace(candidate.ManifestHash)
            && !string.Equals(request.ManifestHash, candidate.ManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("MANIFEST_MISMATCH", "清单哈希与候选不一致", 409);

        // 指令有效性（若携带）
        if (request.CommandId is not null)
        {
            var command = await _db.Commands.FirstOrDefaultAsync(c => c.Id == request.CommandId, ct)
                ?? throw new BusinessException("COMMAND_EXPIRED", "上传指令不存在", 409);
            if (command.ClientId != clientId)
                throw new BusinessException("FORBIDDEN", "指令不属于当前客户端", 403);
            if (command.CommandType is not (CommandType.UploadCandidate or CommandType.UploadLatest))
                throw new BusinessException("UPLOAD_NOT_PERMITTED", "指令类型不是上传指令", 409);
        }

        // 并发与空间
        var maxConcurrent = await _settings.GetIntAsync("max_concurrent_uploads", 2, ct);
        var activeSessions = await _db.UploadSessions
            .CountAsync(s => s.ClientId == clientId && ActiveStatuses.Contains(s.Status), ct);
        if (activeSessions >= maxConcurrent)
            throw new BusinessException("UPLOAD_SESSION_CONFLICT", $"客户端活动上传已达上限（{maxConcurrent}）", 409);

        var requiredBytes = request.TotalBytes > 0 ? request.TotalBytes : candidate.TotalBytes ?? 0;
        var freeBytes = await _storage.GetStagingFreeBytesAsync(ct);
        if (freeBytes < requiredBytes)
            throw new BusinessException("STORAGE_SPACE_LOW", $"暂存空间不足（需 {requiredBytes} 字节，可用 {freeBytes} 字节）", 507);

        // 审计 A-03：这个值原先取出来就没再用过（编译器不报 CS0219，因为它来自方法调用），
        // 于是「会话超时」这件事从头到尾没有任何人执行。现在它有两个真实用途：
        // 下面返回给 Agent 的 ExpiresAt，以及 LifecycleExpiryWorker 的过期判定基准。
        var sessionTimeout = await _settings.GetIntAsync(LifecycleExpiryWorker.SessionTimeoutKey, LifecycleExpiryWorker.DefaultSessionTimeoutSeconds, ct);
        var now = DateTime.UtcNow;

        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = candidate.TaskId,
            CandidateBackupSetId = candidate.Id,
            Status = UploadStatus.Created,
            TotalFiles = request.TotalFiles > 0 ? request.TotalFiles : candidate.Files.Count,
            TotalBytes = requiredBytes,
            ChunkSizeBytes = request.ChunkSizeBytes,
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey,
            StartedAt = now,
            LastActivityAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        // 审查 P1-1：expected_sha256 是服务端唯一的权威完整性基准（来自预检清单）。
        // 缺失时下游无从比对，只能退化为「相信上传方自己声明的哈希」——那等于没有校验。
        // 因此在建会话阶段就拒绝，而不是放行到入库后才发现无从验证。
        var missingHash = candidate.Files
            .Where(f => string.IsNullOrWhiteSpace(f.Sha256))
            .Select(f => f.RelativePath)
            .Take(5)
            .ToList();
        if (missingHash.Count > 0)
        {
            throw new BusinessException(
                "INVALID_REQUEST",
                $"候选文件缺少预检哈希，无法建立上传会话：{string.Join("、", missingHash)}"
                + (candidate.Files.Count(f => string.IsNullOrWhiteSpace(f.Sha256)) > missingHash.Count ? " 等" : ""),
                400);
        }

        // 由候选文件生成上传文件计划
        foreach (var cf in candidate.Files.OrderBy(f => f.SortOrder))
        {
            session.Files.Add(new UploadFileEntity
            {
                Id = Guid.NewGuid(),
                UploadSessionId = session.Id,
                CandidateFileId = cf.Id,
                RelativePath = cf.RelativePath,
                SizeBytes = cf.SizeBytes,
                ExpectedSha256 = cf.Sha256!,
                TotalChunks = (int)Math.Ceiling(cf.SizeBytes / (double)request.ChunkSizeBytes),
                Status = UploadFileStatus.Pending
            });
        }

        // 审计 A-04：暂存目录必须先于 SaveChangesAsync 建好，把路径一起写进 staging_path。
        // staging_path 这一列建表就有、EF 也映射了，却从来没有人给它赋过值；
        // 现在它承担「这个会话的暂存目录还需不需要清」这个职责——非空即待清理，
        // LifecycleExpiryWorker 清完置 null，天然幂等，不必再加一列。
        // 建目录放在写库之前还有一个附带好处：目录建不出来（盘满、没权限）时
        // 直接失败，不会留下一条指向不存在目录的会话记录。
        session.StagingPath = await _storage.EnsureSessionDirectoryAsync(session.Id, ct);

        _db.UploadSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("upload.session.create", AuditResult.Success, "upload_session", session.Id, ct: ct);

        _logger.LogInformation("创建上传会话 session={SessionId} candidate={CandidateId} files={Files}",
            session.Id, candidate.Id, session.Files.Count);

        return BuildCreateResponse(session, resumed: false, sessionTimeout);
    }

    public async Task<MissingChunksResponse> GetMissingChunksAsync(Guid clientId, Guid sessionId, Guid fileId, CancellationToken ct = default)
    {
        var file = await LoadOwnedFileAsync(clientId, sessionId, fileId, ct);

        var received = await _db.UploadChunks
            .Where(c => c.UploadFileId == file.Id && c.Status == UploadChunkStatus.Received)
            .Select(c => c.ChunkIndex)
            .ToListAsync(ct);

        var receivedSet = received.ToHashSet();
        var missing = Enumerable.Range(0, file.TotalChunks).Where(i => !receivedSet.Contains(i)).ToList();

        var response = new MissingChunksResponse { Received = received.OrderBy(i => i).ToList() };

        // 块特别多时返回范围（设计书 14.2）
        if (missing.Count > 1024)
        {
            response.MissingRanges = ToRanges(missing);
        }
        else
        {
            response.Missing = missing;
        }

        return response;
    }

    public async Task<UploadChunkResponse> UploadChunkAsync(
        Guid clientId, Guid sessionId, Guid fileId, int chunkIndex, long offset, string expectedHash, Stream data, CancellationToken ct = default)
    {
        // 一次投影查询同时取回会话与文件的校验字段。
        //
        // 原先是 LoadOwnedSessionAsync + LoadOwnedFileAsync 两次 SELECT，而后者内部
        // 又会把会话再查一遍——每块三次查询，且都进变更跟踪。这条路径每块都要走，
        // 跟踪实体纯属浪费：真正要改的字段最后由一条 SQL 直接更新。
        var ctx = await _db.UploadFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.UploadSessionId,
                f.TotalChunks,
                f.SizeBytes,
                SessionClientId = f.UploadSession.ClientId,
                SessionStatus = f.UploadSession.Status,
                SessionChunkSize = f.UploadSession.ChunkSizeBytes
            })
            .FirstOrDefaultAsync(ct);

        // 查不到文件、或文件不属于这个会话：走回原来的两步路径，
        // 让「会话不存在 / 会话不属于本客户端 / 文件不存在」这三种错误
        // 仍然按原有的顺序和错误码抛出。异常路径多一次查询无所谓。
        if (ctx is null || ctx.UploadSessionId != sessionId)
        {
            await LoadOwnedSessionAsync(clientId, sessionId, ct);
            await LoadOwnedFileAsync(clientId, sessionId, fileId, ct);
            throw new NotFoundException("上传文件", fileId);
        }

        if (ctx.SessionClientId != clientId)
            throw new BusinessException("FORBIDDEN", "会话不属于当前客户端", 403);
        if (ctx.SessionStatus == UploadStatus.Paused)
            throw new BusinessException(PausedErrorCode, "这次传输已被管理员暂停，恢复后会从断点继续", 409);
        if (!WritableStatuses.Contains(ctx.SessionStatus))
            throw new BusinessException("UPLOAD_SESSION_CONFLICT", $"会话状态 {EnumMapping.ToSnakeCase(ctx.SessionStatus)} 不允许写入", 409);

        // 块序号校验
        if (chunkIndex < 0 || chunkIndex >= ctx.TotalChunks)
            throw new BusinessException("CHUNK_INVALID", $"块序号 {chunkIndex} 超出范围（0..{ctx.TotalChunks - 1}）", 400);

        // 偏移校验
        var expectedOffset = (long)chunkIndex * ctx.SessionChunkSize;
        if (offset != expectedOffset)
            throw new BusinessException("CHUNK_INVALID", $"块偏移 {offset} 与期望 {expectedOffset} 不符", 400);

        if (string.IsNullOrWhiteSpace(expectedHash))
            throw new BusinessException("CHUNK_INVALID", "缺少 X-Chunk-SHA256", 400);

        var (serverHash, chunkBytes) = await _storage.WriteChunkAsync(sessionId, fileId, chunkIndex, offset, data, ct);

        // 审查 P1-1：块长度必须与块序号推出的期望值一致。
        // 原先只校验偏移与哈希，不校验长度——非末块传一个短块即可在暂存文件里留下空洞，
        // 而空洞位置的字节是 0，块自身的哈希仍然自洽，只有整文件哈希才会发现。
        // 在这里拦住可以让错误停在出问题的那一块，而不是整份传完再报。
        var isLastChunk = chunkIndex == ctx.TotalChunks - 1;
        var expectedChunkBytes = isLastChunk ? ctx.SizeBytes - offset : ctx.SessionChunkSize;
        if (chunkBytes != expectedChunkBytes)
        {
            await MarkFileFailedAsync(fileId, "CHUNK_INVALID", ct);
            throw new BusinessException(
                "CHUNK_INVALID",
                $"块长度错误（块 {chunkIndex}：期望 {expectedChunkBytes} 字节，实际 {chunkBytes} 字节）", 400);
        }

        if (!string.Equals(serverHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            await MarkFileFailedAsync(fileId, "CHUNK_HASH_MISMATCH", ct);
            throw new BusinessException("CHUNK_HASH_MISMATCH", $"分块哈希错误（块 {chunkIndex}）", 409);
        }

        await RecordReceivedChunkAsync(sessionId, fileId, chunkIndex, offset, (int)chunkBytes, expectedHash, serverHash, ct);

        return new UploadChunkResponse { Received = true, ChunkIndex = chunkIndex, ServerHash = serverHash };
    }

    /// <summary>
    /// 把「这一块已收到」这件事落库：分块记录 upsert + 文件级与会话级计数推进，
    /// 全部在**一条语句、一个事务**里完成。
    ///
    /// 为什么值得写成裸 SQL：原先这一步是 5 次往返 + 2 次事务提交
    /// （SELECT 分块 → SaveChanges → COUNT → SUM → 带 join 的 SUM → SaveChanges）。
    /// 两次提交就是两次 WAL fsync，而 fsync 正是慢存储上最贵的操作——
    /// 和写穿一样，它在虚拟磁盘 / ZFS 无 SLOG 环境里会被放大十几倍。
    ///
    /// 更要命的是那两个 SUM 是全量聚合：每收一块就把该文件已收的所有块重扫一遍，
    /// 于是单块成本随已传块数线性增长，整个文件是 O(n²)。6GB / 8MB = 768 块时，
    /// 最后一块要扫 768 行才能算出一个本可以直接加出来的数。
    ///
    /// 改成增量累加（uploaded_bytes = uploaded_bytes + 本块大小）之后，
    /// 单块成本与已传块数无关，且并发上传多块时也是正确的——
    /// 增量在数据库里做，不存在应用层「读-改-写」的丢更新问题。
    /// </summary>
    internal async Task RecordReceivedChunkAsync(
        Guid sessionId, Guid fileId, int chunkIndex, long offset, int chunkBytes,
        string expectedHash, string serverHash, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var received = EnumMapping.ToSnakeCase(UploadChunkStatus.Received);
        var fileUploading = EnumMapping.ToSnakeCase(UploadFileStatus.Uploading);
        var sessionUploading = EnumMapping.ToSnakeCase(UploadStatus.Uploading);
        var sessionPaused = EnumMapping.ToSnakeCase(UploadStatus.Paused);

        // 三处细节决定这条语句在并发下对不对，都不是随手能省的：
        //
        // 一、`xmax = 0` 是 PostgreSQL 判定「这一行是刚插入的还是被冲突更新的」的标准写法：
        //     新插入的行 xmax 为 0，被 DO UPDATE 命中的行 xmax 是当前事务号。
        //     用它把重传识别出来——同一块传两次，字节数只能计一次，
        //     否则进度会超过 100%、ETA 变成负数，而进度是判断「还要多久」的唯一依据。
        //
        // 二、会话那条 UPDATE 的增量取自 `f` 的 RETURNING，而不是另起一个 delta 子查询。
        //     这不是写法偏好：PostgreSQL 文档写明数据修改型 CTE 之间的执行顺序是**未定义**的，
        //     两条互不依赖的 UPDATE 谁先谁后不作保证。而客户端现在会并行传同一个文件的多块，
        //     多个后端会同时更新同一行 upload_files 和同一行 upload_sessions——
        //     一旦两个后端的加锁顺序相反就是死锁。让会话的更新在数据上依赖文件的更新，
        //     执行顺序被钉死为 upload_chunks → upload_files → upload_sessions，
        //     所有后端顺序一致，死锁在结构上不可能发生。
        //
        //     COALESCE 兜住 `f` 一行都没更新的情况（文件 id 不存在）：
        //     少了它 `uploaded_bytes + NULL` 会把整列写成 NULL，
        //     而这一列是进度和续传判定的依据。
        //
        // 三、status 那一列必须**条件**写回 uploading，不能无条件赋值。
        //     这里原先是一句 `status = 'uploading'`，而它是「点了暂停几秒后传输自己
        //     又跑起来」的根因：管理端把会话置 paused 之后，任何一个已经通过写入校验、
        //     还在路上的分块落库，都会把状态改回 uploading——而 Agent 默认 4 路并行、
        //     每块 8MB，点暂停的那一刻几乎必然有在途块。于是暂停从未真正生效过一次。
        //
        //     字节数与 last_activity_at 仍然照常累加：这一块确实收到了、确实落在暂存
        //     目录里，进度不能倒退，否则恢复后 missing-chunks 与 uploaded_bytes 会对不上。
        //     被保护的只有状态本身。
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH ins AS (
                INSERT INTO upload_chunks (
                    id, upload_file_id, chunk_index, offset_bytes,
                    size_bytes, expected_hash, server_hash, status, received_at)
                VALUES (
                    gen_random_uuid(), {fileId}, {chunkIndex}, {offset},
                    {chunkBytes}, {expectedHash}, {serverHash}, {received}, {now})
                ON CONFLICT (upload_file_id, chunk_index) DO UPDATE SET
                    offset_bytes  = EXCLUDED.offset_bytes,
                    size_bytes    = EXCLUDED.size_bytes,
                    expected_hash = EXCLUDED.expected_hash,
                    server_hash   = EXCLUDED.server_hash,
                    status        = EXCLUDED.status,
                    received_at   = EXCLUDED.received_at
                RETURNING (xmax = 0) AS is_new, size_bytes
            ),
            f AS (
                UPDATE upload_files SET
                    uploaded_chunks = uploaded_chunks
                        + (SELECT CASE WHEN is_new THEN 1 ELSE 0 END FROM ins),
                    uploaded_bytes  = uploaded_bytes
                        + (SELECT CASE WHEN is_new THEN size_bytes ELSE 0 END FROM ins),
                    status          = {fileUploading}
                WHERE id = {fileId}
                RETURNING (SELECT CASE WHEN is_new THEN size_bytes ELSE 0 END FROM ins) AS bytes
            )
            UPDATE upload_sessions SET
                uploaded_bytes   = uploaded_bytes + COALESCE((SELECT bytes FROM f), 0),
                status           = CASE WHEN status = {sessionPaused} THEN status ELSE {sessionUploading} END,
                last_activity_at = {now},
                updated_at       = {now}
            WHERE id = {sessionId}
            """, ct);
    }

    public async Task<CompleteUploadFileResponse> CompleteFileAsync(Guid clientId, Guid sessionId, Guid fileId, CompleteUploadFileRequest request, CancellationToken ct = default)
    {
        var session = await LoadOwnedSessionAsync(clientId, sessionId, ct);
        var file = await LoadOwnedFileAsync(clientId, sessionId, fileId, ct);

        // 暂停中不许继续推进。分块写入被拦住了，但若暂停发生在最后一块之后，
        // 这条路径仍然畅通——会话会一路走到 verifying 再入库，暂停等于没按。
        if (session.Status == UploadStatus.Paused)
            throw new BusinessException(PausedErrorCode, "这次传输已被管理员暂停，恢复后会从断点继续", 409);

        if (file.Status is UploadFileStatus.Verified or UploadFileStatus.Received)
        {
            return new CompleteUploadFileResponse
            {
                UploadFileId = file.Id,
                Status = EnumMapping.ToSnakeCase(file.Status),
                ServerSha256 = file.ServerSha256
            };
        }

        var receivedChunks = await _db.UploadChunks.CountAsync(c => c.UploadFileId == file.Id && c.Status == UploadChunkStatus.Received, ct);
        if (receivedChunks < file.TotalChunks)
            throw new BusinessException("CHUNK_INVALID", $"分块未收齐（{receivedChunks}/{file.TotalChunks}）", 409);

        var (exists, length) = await _storage.GetStagedFileInfoAsync(sessionId, file.Id, ct);
        if (!exists)
            throw new BusinessException("CHUNK_INVALID", "暂存文件缺失", 409);

        var serverSha = await _storage.ComputeFileHashAsync(sessionId, file.Id, ct);
        var now = DateTime.UtcNow;

        // 审查 P1-1：完整性判定以预检清单的 expected_sha256 为准（权威基准，服务端持有）。
        // 客户端在完成时声明的 request.Sha256 只作附加交叉验证——它由上传方单方面给出，
        // 原先「不传就整段跳过」等于把完整性开关交到了被验证方手里。
        var mismatchReason =
            !string.Equals(serverSha, file.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
                ? $"实测哈希与预检期望值不一致（期望 {file.ExpectedSha256}，实测 {serverSha}）"
            : !string.IsNullOrWhiteSpace(request.Sha256)
              && !string.Equals(serverSha, request.Sha256, StringComparison.OrdinalIgnoreCase)
                ? $"客户端声明哈希与实测不一致（声明 {request.Sha256}，实测 {serverSha}）"
            : null;

        if (mismatchReason is not null)
        {
            file.Status = UploadFileStatus.Failed;
            file.ErrorCode = "FILE_HASH_MISMATCH";
            file.ServerSha256 = serverSha;
            session.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync("upload.file.complete", AuditResult.Failure, "upload_session", session.Id,
                errorCode: "FILE_HASH_MISMATCH",
                errorMessage: $"文件哈希错误 {file.RelativePath}：{mismatchReason}", ct: ct);
            throw new BusinessException(
                "FILE_HASH_MISMATCH", $"文件哈希错误（{file.RelativePath}）：{mismatchReason}", 409);
        }

        file.Status = UploadFileStatus.Verified;
        file.ServerSha256 = serverSha;
        file.SizeBytes = length;
        session.VerifiedBytes = await _db.UploadFiles
            .Where(f => f.UploadSessionId == session.Id && f.Status == UploadFileStatus.Verified)
            .SumAsync(f => f.SizeBytes, ct) + length;
        session.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return new CompleteUploadFileResponse
        {
            UploadFileId = file.Id,
            Status = EnumMapping.ToSnakeCase(file.Status),
            ServerSha256 = serverSha
        };
    }

    public async Task<CompleteUploadSessionResponse> CompleteSessionAsync(Guid clientId, Guid sessionId, CompleteUploadSessionRequest request, CancellationToken ct = default)
    {
        var session = await LoadOwnedSessionAsync(clientId, sessionId, ct);

        // 与 CompleteFileAsync 同一个理由：暂停必须挡住所有向前推进的路径，
        // 而不只是分块写入那一条。
        if (session.Status == UploadStatus.Paused)
            throw new BusinessException(PausedErrorCode, "这次传输已被管理员暂停，恢复后会从断点继续", 409);

        if (session.Status is UploadStatus.Verifying or UploadStatus.Verified or UploadStatus.Committed)
        {
            return new CompleteUploadSessionResponse
            {
                Status = EnumMapping.ToSnakeCase(session.Status),
                VerificationOperationId = session.Id
            };
        }

        var candidate = await _db.Set<Core.Entities.Backup.CandidateBackupSet>()
            .AsNoTracking().FirstAsync(c => c.Id == session.CandidateBackupSetId, ct);

        // manifest 一致性
        if (!string.IsNullOrWhiteSpace(request.ManifestHash)
            && !string.IsNullOrWhiteSpace(candidate.ManifestHash)
            && !string.Equals(request.ManifestHash, candidate.ManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("MANIFEST_MISMATCH", "清单哈希不一致", 409);

        var files = await _db.UploadFiles.Where(f => f.UploadSessionId == session.Id).ToListAsync(ct);
        var unverified = files.Where(f => f.Status is not (UploadFileStatus.Verified or UploadFileStatus.Received)).ToList();
        if (unverified.Count > 0)
            throw new BusinessException("UPLOAD_SESSION_CONFLICT", $"仍有 {unverified.Count} 个文件未完成校验", 409);

        var now = DateTime.UtcNow;
        session.Status = UploadStatus.Verifying;
        session.CompletedAt = now;
        session.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        await _commitQueue.Writer.WriteAsync(new WorkItem(WorkKind.CommitSession, session.Id), ct);
        await _audit.RecordAsync("upload.session.complete", AuditResult.Success, "upload_session", session.Id, ct: ct);

        _logger.LogInformation("会话 {SessionId} 完成接收，进入校验入库队列", session.Id);

        return new CompleteUploadSessionResponse
        {
            Status = "verifying",
            VerificationOperationId = session.Id
        };
    }

    public async Task<UploadSessionStatusResponse> GetSessionStatusAsync(Guid clientId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await LoadOwnedSessionAsync(clientId, sessionId, ct);
        var files = await _db.UploadFiles.Where(f => f.UploadSessionId == session.Id).OrderBy(f => f.RelativePath).ToListAsync(ct);

        var response = new UploadSessionStatusResponse
        {
            UploadSessionId = session.Id,
            Status = EnumMapping.ToSnakeCase(session.Status),
            UploadedBytes = session.UploadedBytes,
            TotalBytes = session.TotalBytes,
            TotalFiles = session.TotalFiles,
            ErrorCode = session.ErrorCode,
            ErrorMessage = session.ErrorMessage,
            Resumable = ActiveStatuses.Contains(session.Status) || session.Status == UploadStatus.Verifying
        };

        // 审计 H-18：整个会话的已收分块一次查回来按文件分组，不再每个文件查一次。
        // 原先是循环里逐文件 ToListAsync——一个 500 文件的会话就是 500 次数据库往返，
        // 而这个接口是 Agent 断点续传时必调的第一步。
        var receivedByFile = await _db.UploadChunks
            .Where(c => c.UploadFile.UploadSessionId == session.Id && c.Status == UploadChunkStatus.Received)
            .Select(c => new { c.UploadFileId, c.ChunkIndex })
            .ToListAsync(ct);
        var receivedLookup = receivedByFile
            .GroupBy(c => c.UploadFileId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.ChunkIndex).ToHashSet());

        foreach (var file in files)
        {
            var dto = new UploadFileProgressDto
            {
                UploadFileId = file.Id,
                RelativePath = file.RelativePath,
                Status = EnumMapping.ToSnakeCase(file.Status),
                UploadedBytes = file.UploadedBytes,
                SizeBytes = file.SizeBytes,
                UploadedChunks = file.UploadedChunks,
                TotalChunks = file.TotalChunks
            };

            if (file.Status is UploadFileStatus.Pending or UploadFileStatus.Uploading)
            {
                var received = receivedLookup.GetValueOrDefault(file.Id) ?? [];
                var missing = Enumerable.Range(0, file.TotalChunks).Where(i => !received.Contains(i)).ToList();
                if (missing.Count <= 100)
                    dto.MissingChunks = missing;
            }

            response.Files.Add(dto);
        }

        return response;
    }

    public async Task CancelSessionAsync(Guid clientId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await LoadOwnedSessionAsync(clientId, sessionId, ct);

        if (session.Status is UploadStatus.Committed)
            throw new BusinessException("CONFLICT", "会话已入库，无法取消", 409);
        if (session.Status is UploadStatus.Cancelled)
            return;

        session.Status = UploadStatus.Cancelled;
        session.CompletedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // 取消后临时文件按 staging_cleanup_retention_hours 保留一段时间供排障，
        // 到期由 LifecycleExpiryWorker 清理（审计 A-04）。原注释说的「定时清理任务」
        // 在写下那句话的时候并不存在，现在是真的了。
        await _audit.RecordAsync("upload.session.cancel", AuditResult.Success, "upload_session", session.Id, ct: ct);
    }

    public async Task InterruptSessionAsync(
        Guid clientId, Guid sessionId, InterruptUploadSessionRequest request, CancellationToken ct = default)
    {
        var session = await LoadOwnedSessionAsync(clientId, sessionId, ct);

        // 已经终结的会话不回头：入库完了、被人取消了、被管理端暂停了，都不该由 Agent 改写
        if (session.Status is UploadStatus.Committed or UploadStatus.Cancelled
            or UploadStatus.Expired or UploadStatus.Paused)
            return;

        session.Status = UploadStatus.RetryWait;
        session.ErrorCode = Truncate(request.ErrorCode, 64);
        session.ErrorMessage = Truncate(request.ErrorMessage, 2000);
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("upload.session.interrupt", AuditResult.Success, "upload_session", session.Id, ct: ct);

        _logger.LogWarning("上传会话 {SessionId} 中断，暂存与已传分块保留以便续传：{Code} {Message}",
            session.Id, request.ErrorCode, request.ErrorMessage);
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : (value.Length <= maxLength ? value : value[..maxLength]);

    private async Task<UploadSession> LoadOwnedSessionAsync(Guid clientId, Guid sessionId, CancellationToken ct)
    {
        var session = await _db.UploadSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new NotFoundException("上传会话", sessionId);
        if (session.ClientId != clientId)
            throw new BusinessException("FORBIDDEN", "会话不属于当前客户端", 403);
        return session;
    }

    private async Task<UploadFileEntity> LoadOwnedFileAsync(Guid clientId, Guid sessionId, Guid fileId, CancellationToken ct)
    {
        var session = await LoadOwnedSessionAsync(clientId, sessionId, ct);
        var file = await _db.UploadFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.UploadSessionId == session.Id, ct)
            ?? throw new NotFoundException("上传文件", fileId);
        return file;
    }

    /// <summary>
    /// 把文件标记为失败。按 id 直接更新而不是先加载实体：调用点（分块校验失败）
    /// 已经不再持有跟踪实体，而这里也没必要为了改两个字段把整行读回来。
    /// </summary>
    private async Task MarkFileFailedAsync(Guid fileId, string errorCode, CancellationToken ct)
    {
        var failed = EnumMapping.ToSnakeCase(UploadFileStatus.Failed);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE upload_files SET status = {failed}, error_code = {errorCode} WHERE id = {fileId}", ct);
    }

    private static List<ChunkRangeDto> ToRanges(List<int> sorted)
    {
        var ranges = new List<ChunkRangeDto>();
        if (sorted.Count == 0)
            return ranges;

        var start = sorted[0];
        var end = sorted[0];
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == end + 1)
            {
                end = sorted[i];
            }
            else
            {
                ranges.Add(new ChunkRangeDto { Start = start, End = end });
                start = end = sorted[i];
            }
        }
        ranges.Add(new ChunkRangeDto { Start = start, End = end });
        return ranges;
    }

    /// <summary>
    /// 组装建会话应答。
    ///
    /// 审计 A-03：ExpiresAt 原先硬编码 AddSeconds(3600)，是给 Agent 看的装饰——
    /// 服务端自己既不认这个值也没有过期机制。现在它和 LifecycleExpiryWorker
    /// 用的是同一个 upload_session_timeout_seconds，两端说的是同一件事。
    /// </summary>
    private static CreateUploadSessionResponse BuildCreateResponse(UploadSession session, bool resumed, int timeoutSeconds)
    {
        return new CreateUploadSessionResponse
        {
            UploadSessionId = session.Id,
            Status = resumed ? "resumed" : "created",
            ChunkSizeBytes = session.ChunkSizeBytes,
            ExpiresAt = (session.LastActivityAt ?? session.CreatedAt).AddSeconds(timeoutSeconds),
            Files = session.Files.Select(f => new UploadSessionFileDto
            {
                UploadFileId = f.Id,
                RelativePath = f.RelativePath,
                MissingChunks = "all"
            }).ToList()
        };
    }
}
