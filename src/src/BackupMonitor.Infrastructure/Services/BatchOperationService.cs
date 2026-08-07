using System.Text.Json;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>管理端批量操作服务（设计书 17 批量操作接口）</summary>
public interface IBatchOperationService
{
    Task<CreatePrecheckBatchResponse> CreatePrecheckBatchAsync(CreatePrecheckBatchRequest request, CancellationToken ct = default);
    Task<UploadBatchDto> CreateUploadBatchAsync(CreateUploadBatchRequest request, string? idempotencyKey, CancellationToken ct = default);
    Task<PagedResult<UploadBatchDto>> ListBatchesAsync(PagedQuery query, CancellationToken ct = default);
    Task<UploadBatchDto> GetBatchAsync(Guid batchId, CancellationToken ct = default);
}

/// <summary>批量操作实现（批量预检 / 批量上传幂等创建 / 批次查询）</summary>
public class BatchOperationService : IBatchOperationService
{
    private static readonly UploadStatus[] ActiveUploadStatuses =
    [
        UploadStatus.Created, UploadStatus.WaitingPermission, UploadStatus.Uploading,
        UploadStatus.Paused, UploadStatus.RetryWait, UploadStatus.Received, UploadStatus.Verifying
    ];

    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _commands;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<BatchOperationService> _logger;

    public BatchOperationService(
        AppDbContext db,
        ICommandDispatcher commands,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<BatchOperationService> logger)
    {
        _db = db;
        _commands = commands;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>批量预检（设计书 17.1）：按范围解析任务并逐个下发预检指令</summary>
    public async Task<CreatePrecheckBatchResponse> CreatePrecheckBatchAsync(CreatePrecheckBatchRequest request, CancellationToken ct = default)
    {
        var scope = request.Scope;
        var hasScope = (scope.ClientGroupIds is { Count: > 0 })
                       || (scope.ClientIds is { Count: > 0 })
                       || (scope.TaskIds is { Count: > 0 })
                       || (scope.ApplicationNames is { Count: > 0 });
        if (!hasScope)
            throw new ValidationFailedException("批量操作范围不能为空（至少提供分组/客户端/任务/应用名之一）");

        var tasks = _db.BackupTasks.AsNoTracking()
            .Include(t => t.Client)
            .AsQueryable();

        if (scope.TaskIds is { Count: > 0 })
            tasks = tasks.Where(t => scope.TaskIds.Contains(t.Id));
        if (scope.ClientIds is { Count: > 0 })
            tasks = tasks.Where(t => scope.ClientIds.Contains(t.ClientId));
        if (scope.ClientGroupIds is { Count: > 0 })
            tasks = tasks.Where(t => t.Client.ClientGroupId != null && scope.ClientGroupIds.Contains(t.Client.ClientGroupId.Value));
        if (scope.ApplicationNames is { Count: > 0 })
            tasks = tasks.Where(t => scope.ApplicationNames.Contains(t.ApplicationName));

        var candidates = await tasks.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt).ToListAsync(ct);

        var response = new CreatePrecheckBatchResponse();
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var task in candidates)
        {
            // 跳过条件：仅启用任务开关、客户端已禁用/注销/未审批
            var skipped = (request.OnlyEnabled && !task.Enabled)
                          || task.Client.Status is ClientStatus.Disabled or ClientStatus.Revoked or ClientStatus.PendingApproval;
            if (skipped)
            {
                response.SkippedTasks++;
                continue;
            }

            var command = await _commands.CreateCommandAsync(
                task.ClientId, CommandType.PrecheckTask,
                taskId: task.Id,
                priority: task.Priority,
                idempotencyKey: $"batch-precheck:{stamp}:{task.Id}",
                createdBy: _context.UserId,
                ct: ct);

            response.CommandIds.Add(command.Id);
            response.DispatchedCommands++;
        }

        await _audit.RecordAsync("operations.precheck_batch", AuditResult.Success, "batch", null,
            afterData: JsonSerializer.Serialize(new
            {
                response.DispatchedCommands,
                response.SkippedTasks,
                request.OnlyEnabled
            }), ct: ct);

        _logger.LogInformation("批量预检已下发 {Dispatched} 条指令，跳过 {Skipped} 个任务",
            response.DispatchedCommands, response.SkippedTasks);

        return response;
    }

    /// <summary>批量上传（设计书 17.2，支持 Idempotency-Key 幂等）</summary>
    public async Task<UploadBatchDto> CreateUploadBatchAsync(CreateUploadBatchRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        // 幂等：相同 Idempotency-Key 返回原批次（设计书 8.5）
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await _db.UploadBatches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                _logger.LogInformation("幂等键 {Key} 命中既有批次 {BatchId}，直接返回", idempotencyKey, existing.Id);
                return await GetBatchAsync(existing.Id, ct);
            }
        }

        var distinctIds = request.CandidateBackupSetIds.Distinct().ToList();

        var candidates = await _db.CandidateBackupSets.AsNoTracking()
            .Include(c => c.Client)
            .Include(c => c.Task)
            .Where(c => distinctIds.Contains(c.Id))
            .ToListAsync(ct);

        var missing = distinctIds.Except(candidates.Select(c => c.Id)).ToList();
        if (missing.Count > 0)
            throw new NotFoundException("候选备份集", string.Join(",", missing));

        var now = DateTime.UtcNow;
        var busyClientIds = await _db.UploadSessions
            .Where(s => ActiveUploadStatuses.Contains(s.Status))
            .Select(s => s.ClientId)
            .Distinct()
            .ToListAsync(ct);
        var busySet = busyClientIds.ToHashSet();

        var accepted = new List<Core.Entities.Backup.CandidateBackupSet>();
        var skippedReasons = new List<string>();

        foreach (var candidate in candidates.OrderBy(c => c.Task.Priority))
        {
            if (candidate.PrecheckStatus != PrecheckStatus.Passed)
            {
                skippedReasons.Add($"{candidate.CandidateKey}: 预检未通过");
                continue;
            }
            if (candidate.ExpiresAt is not null && candidate.ExpiresAt < now)
            {
                skippedReasons.Add($"{candidate.CandidateKey}: 候选已过期");
                continue;
            }
            if (candidate.SupersededById is not null)
            {
                skippedReasons.Add($"{candidate.CandidateKey}: 已被新候选替代");
                continue;
            }
            if (await _db.BackupSets.AnyAsync(s => s.SourceCandidateId == candidate.Id, ct))
            {
                skippedReasons.Add($"{candidate.CandidateKey}: 已入库");
                continue;
            }
            if (candidate.Client.Status is ClientStatus.Disabled or ClientStatus.Revoked or ClientStatus.PendingApproval)
            {
                skippedReasons.Add($"{candidate.CandidateKey}: 客户端不可用");
                continue;
            }
            if (request.SkipBusyClients && busySet.Contains(candidate.ClientId))
            {
                skippedReasons.Add($"{candidate.CandidateKey}: 客户端忙碌（活动上传中）");
                continue;
            }

            accepted.Add(candidate);
        }

        if (accepted.Count == 0)
            throw new BusinessException("INVALID_REQUEST",
                $"没有可上传的候选备份集。跳过原因：{string.Join("；", skippedReasons)}", 422);

        var batch = new Core.Entities.Upload.UploadBatch
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? $"批量上传 {now:yyyy-MM-dd HH:mm}"
                : request.Name.Trim(),
            CreatedBy = _context.UserId,
            MaxConcurrentClients = Math.Clamp(request.MaxConcurrentClients, 1, 50),
            MaxConcurrentPerClient = Math.Clamp(request.MaxConcurrentPerClient, 1, 10),
            BandwidthLimitKbps = request.TemporaryBandwidthLimitKbps,
            Status = BatchStatus.Pending,
            TotalItems = accepted.Count,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? null
                : idempotencyKey.Trim()[..Math.Min(idempotencyKey.Trim().Length, 128)]
        };

        _db.UploadBatches.Add(batch);
        await _db.SaveChangesAsync(ct);

        // 逐候选下发上传指令（幂等键绑定批次，重复创建不会二次下发）
        foreach (var candidate in accepted)
        {
            await _commands.CreateCommandAsync(
                candidate.ClientId, CommandType.UploadCandidate,
                taskId: candidate.TaskId,
                candidateBackupSetId: candidate.Id,
                payload: new
                {
                    candidateBackupSetId = candidate.Id,
                    batchId = batch.Id,
                    bandwidthLimitKbps = batch.BandwidthLimitKbps ?? candidate.Task.BandwidthLimitKbps,
                    chunkSizeBytes = candidate.Task.ChunkSizeBytes,
                    maxConcurrentPerClient = batch.MaxConcurrentPerClient
                },
                priority: candidate.Task.Priority,
                idempotencyKey: $"batch-upload:{batch.Id}:{candidate.Id}",
                createdBy: _context.UserId,
                ct: ct);
        }

        await _audit.RecordAsync("operations.upload_batch", AuditResult.Success, "upload_batch", batch.Id,
            afterData: JsonSerializer.Serialize(new
            {
                batch.Id,
                batch.TotalItems,
                skipped = skippedReasons.Count
            }), ct: ct);

        _logger.LogInformation("批量上传批次 {BatchId} 已创建：{Total} 项，跳过 {Skipped} 项",
            batch.Id, batch.TotalItems, skippedReasons.Count);

        return await GetBatchAsync(batch.Id, ct);
    }

    public async Task<PagedResult<UploadBatchDto>> ListBatchesAsync(PagedQuery query, CancellationToken ct = default)
    {
        var batches = _db.UploadBatches.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Keyword))
            batches = batches.Where(b => b.Name != null && EF.Functions.ILike(b.Name, $"%{query.Keyword.Trim()}%"));

        var totalCount = await batches.LongCountAsync(ct);

        var ids = await batches
            .OrderByDescending(b => b.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(b => b.Id)
            .ToListAsync(ct);

        var items = new List<UploadBatchDto>();
        foreach (var id in ids)
            items.Add(await GetBatchAsync(id, ct));

        return PagedResult<UploadBatchDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    /// <summary>批次详情（设计书 17.3，含单项指令/会话状态）</summary>
    public async Task<UploadBatchDto> GetBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _db.UploadBatches.AsNoTracking()
            .Include(b => b.CreatedByUser)
            .FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new NotFoundException("批量上传批次", batchId);

        // 批次单项：以批次内上传会话为主线；尚未创建会话的候选以指令幂等键补齐
        var sessions = await _db.UploadSessions.AsNoTracking()
            .Include(s => s.Client)
            .Include(s => s.Task)
            .Include(s => s.CandidateBackupSet)
            .Where(s => s.UploadBatchId == batchId)
            .ToListAsync(ct);

        // 批次下发但客户端尚未创建会话的候选：从指令反查
        var candidateIdsInSessions = sessions.Select(s => s.CandidateBackupSetId).ToHashSet();
        var pendingCommands = await _db.Commands.AsNoTracking()
            .Include(c => c.Client)
            .Include(c => c.CandidateBackupSet)
            .Where(c => c.IdempotencyKey != null && c.IdempotencyKey.StartsWith($"batch-upload:{batchId}:"))
            .ToListAsync(ct);

        var commandByCandidate = pendingCommands
            .Where(c => c.CandidateBackupSetId is not null)
            .GroupBy(c => c.CandidateBackupSetId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CreatedAt).First());

        var items = new List<UploadBatchItemDto>();

        foreach (var session in sessions)
        {
            commandByCandidate.TryGetValue(session.CandidateBackupSetId, out var command);
            items.Add(new UploadBatchItemDto
            {
                CandidateBackupSetId = session.CandidateBackupSetId,
                CandidateKey = session.CandidateBackupSet.CandidateKey,
                ClientId = session.ClientId,
                ClientHostname = session.Client.Hostname,
                CommandId = command?.Id,
                CommandStatus = command is not null ? EnumMapping.ToSnakeCase(command.Status) : null,
                UploadSessionId = session.Id,
                UploadSessionStatus = EnumMapping.ToSnakeCase(session.Status)
            });
        }

        foreach (var command in pendingCommands.Where(c =>
                     c.CandidateBackupSetId is not null
                     && !candidateIdsInSessions.Contains(c.CandidateBackupSetId.Value)))
        {
            items.Add(new UploadBatchItemDto
            {
                CandidateBackupSetId = command.CandidateBackupSetId!.Value,
                CandidateKey = command.CandidateBackupSet?.CandidateKey ?? string.Empty,
                ClientId = command.ClientId,
                ClientHostname = command.Client.Hostname,
                CommandId = command.Id,
                CommandStatus = EnumMapping.ToSnakeCase(command.Status)
            });
        }

        return new UploadBatchDto
        {
            Id = batch.Id,
            Name = batch.Name,
            CreatedBy = batch.CreatedBy,
            CreatedByName = batch.CreatedByUser?.DisplayName,
            Status = EnumMapping.ToSnakeCase(batch.Status),
            MaxConcurrentClients = batch.MaxConcurrentClients,
            MaxConcurrentPerClient = batch.MaxConcurrentPerClient,
            BandwidthLimitKbps = batch.BandwidthLimitKbps,
            TotalItems = batch.TotalItems,
            SucceededItems = batch.SucceededItems,
            FailedItems = batch.FailedItems,
            CreatedAt = batch.CreatedAt,
            CompletedAt = batch.CompletedAt,
            Items = items.OrderBy(i => i.ClientHostname).ToList()
        };
    }
}
