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
}

/// <summary>上传会话实现</summary>
public class UploadSessionService : IUploadSessionService
{
    private static readonly UploadStatus[] WritableStatuses =
        [UploadStatus.Created, UploadStatus.Uploading, UploadStatus.Paused, UploadStatus.RetryWait];

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
                return BuildCreateResponse(existing, resumed: true);
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
            .CountAsync(s => s.ClientId == clientId && WritableStatuses.Contains(s.Status), ct);
        if (activeSessions >= maxConcurrent)
            throw new BusinessException("UPLOAD_SESSION_CONFLICT", $"客户端活动上传已达上限（{maxConcurrent}）", 409);

        var requiredBytes = request.TotalBytes > 0 ? request.TotalBytes : candidate.TotalBytes ?? 0;
        var freeBytes = await _storage.GetStagingFreeBytesAsync(ct);
        if (freeBytes < requiredBytes)
            throw new BusinessException("STORAGE_SPACE_LOW", $"暂存空间不足（需 {requiredBytes} 字节，可用 {freeBytes} 字节）", 507);

        var sessionTimeout = await _settings.GetIntAsync("upload_session_timeout_seconds", 3600, ct);
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
                ExpectedSha256 = cf.Sha256 ?? string.Empty,
                TotalChunks = (int)Math.Ceiling(cf.SizeBytes / (double)request.ChunkSizeBytes),
                Status = UploadFileStatus.Pending
            });
        }

        _db.UploadSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        await _storage.EnsureSessionDirectoryAsync(session.Id, ct);
        await _audit.RecordAsync("upload.session.create", AuditResult.Success, "upload_session", session.Id, ct: ct);

        _logger.LogInformation("创建上传会话 session={SessionId} candidate={CandidateId} files={Files}",
            session.Id, candidate.Id, session.Files.Count);

        return BuildCreateResponse(session, resumed: false);
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
        var session = await LoadOwnedSessionAsync(clientId, sessionId, ct);
        if (!WritableStatuses.Contains(session.Status))
            throw new BusinessException("UPLOAD_SESSION_CONFLICT", $"会话状态 {EnumMapping.ToSnakeCase(session.Status)} 不允许写入", 409);

        var file = await LoadOwnedFileAsync(clientId, sessionId, fileId, ct);

        // 块序号校验
        if (chunkIndex < 0 || chunkIndex >= file.TotalChunks)
            throw new BusinessException("CHUNK_INVALID", $"块序号 {chunkIndex} 超出范围（0..{file.TotalChunks - 1}）", 400);

        // 偏移校验
        var expectedOffset = (long)chunkIndex * session.ChunkSizeBytes;
        if (offset != expectedOffset)
            throw new BusinessException("CHUNK_INVALID", $"块偏移 {offset} 与期望 {expectedOffset} 不符", 400);

        if (string.IsNullOrWhiteSpace(expectedHash))
            throw new BusinessException("CHUNK_INVALID", "缺少 X-Chunk-SHA256", 400);

        var (serverHash, chunkBytes) = await _storage.WriteChunkAsync(sessionId, file.Id, chunkIndex, offset, data, ct);

        if (!string.Equals(serverHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            await MarkFileFailedAsync(file, "CHUNK_HASH_MISMATCH", ct);
            throw new BusinessException("CHUNK_HASH_MISMATCH", $"分块哈希错误（块 {chunkIndex}）", 409);
        }

        var now = DateTime.UtcNow;

        // upsert 分块记录（以实际写入字节数为块大小）
        var chunk = await _db.UploadChunks.FirstOrDefaultAsync(c => c.UploadFileId == file.Id && c.ChunkIndex == chunkIndex, ct);
        if (chunk is null)
        {
            chunk = new UploadChunk
            {
                Id = Guid.NewGuid(),
                UploadFileId = file.Id,
                ChunkIndex = chunkIndex
            };
            _db.UploadChunks.Add(chunk);
        }
        chunk.OffsetBytes = offset;
        chunk.SizeBytes = (int)chunkBytes;
        chunk.ExpectedHash = expectedHash;
        chunk.ServerHash = serverHash;
        chunk.Status = UploadChunkStatus.Received;
        chunk.ReceivedAt = now;

        file.Status = UploadFileStatus.Uploading;
        session.Status = UploadStatus.Uploading;
        session.LastActivityAt = now;
        session.UpdatedAt = now;

        // 先持久化分块记录，确保后续聚合查询包含本次分块
        await _db.SaveChangesAsync(ct);

        file.UploadedChunks = await _db.UploadChunks
            .CountAsync(c => c.UploadFileId == file.Id && c.Status == UploadChunkStatus.Received, ct);
        file.UploadedBytes = await _db.UploadChunks
            .Where(c => c.UploadFileId == file.Id && c.Status == UploadChunkStatus.Received)
            .SumAsync(c => (long)c.SizeBytes, ct);
        // 会话级按分块粒度汇总，避免依赖尚未保存的 file.UploadedBytes
        session.UploadedBytes = await _db.UploadChunks
            .Where(c => c.UploadFile.UploadSessionId == session.Id && c.Status == UploadChunkStatus.Received)
            .SumAsync(c => (long)c.SizeBytes, ct);

        await _db.SaveChangesAsync(ct);

        return new UploadChunkResponse { Received = true, ChunkIndex = chunkIndex, ServerHash = serverHash };
    }

    public async Task<CompleteUploadFileResponse> CompleteFileAsync(Guid clientId, Guid sessionId, Guid fileId, CompleteUploadFileRequest request, CancellationToken ct = default)
    {
        var session = await LoadOwnedSessionAsync(clientId, sessionId, ct);
        var file = await LoadOwnedFileAsync(clientId, sessionId, fileId, ct);

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

        if (!string.IsNullOrWhiteSpace(request.Sha256)
            && !string.Equals(serverSha, request.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            file.Status = UploadFileStatus.Failed;
            file.ErrorCode = "FILE_HASH_MISMATCH";
            file.ServerSha256 = serverSha;
            session.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync("upload.file.complete", AuditResult.Failure, "upload_session", session.Id,
                errorCode: "FILE_HASH_MISMATCH", errorMessage: $"文件哈希错误 {file.RelativePath}", ct: ct);
            throw new BusinessException("FILE_HASH_MISMATCH", $"文件哈希错误（{file.RelativePath}）", 409);
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
            Resumable = WritableStatuses.Contains(session.Status) || session.Status == UploadStatus.Verifying
        };

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
                var received = await _db.UploadChunks
                    .Where(c => c.UploadFileId == file.Id && c.Status == UploadChunkStatus.Received)
                    .Select(c => c.ChunkIndex).ToListAsync(ct);
                var missing = Enumerable.Range(0, file.TotalChunks).Except(received).ToList();
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

        // 取消后临时文件按策略保留（由定时清理任务处理，设计书 14.7）
        await _audit.RecordAsync("upload.session.cancel", AuditResult.Success, "upload_session", session.Id, ct: ct);
    }

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

    private async Task MarkFileFailedAsync(UploadFileEntity file, string errorCode, CancellationToken ct)
    {
        file.Status = UploadFileStatus.Failed;
        file.ErrorCode = errorCode;
        await _db.SaveChangesAsync(ct);
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

    private static CreateUploadSessionResponse BuildCreateResponse(UploadSession session, bool resumed)
    {
        return new CreateUploadSessionResponse
        {
            UploadSessionId = session.Id,
            Status = resumed ? "resumed" : "created",
            ChunkSizeBytes = session.ChunkSizeBytes,
            ExpiresAt = session.LastActivityAt?.AddSeconds(3600),
            Files = session.Files.Select(f => new UploadSessionFileDto
            {
                UploadFileId = f.Id,
                RelativePath = f.RelativePath,
                MissingChunks = "all"
            }).ToList()
        };
    }
}
