using System.Text.Json;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>指令下发器（管理端/批量操作/系统动作共用，设计书 8.4 指令队列）</summary>
public interface ICommandDispatcher
{
    /// <summary>
    /// 创建一条待认领指令。提供 idempotencyKey 时重复下发返回原指令（设计书 8.5）。
    /// </summary>
    Task<Command> CreateCommandAsync(
        Guid clientId,
        CommandType commandType,
        Guid? taskId = null,
        Guid? candidateBackupSetId = null,
        object? payload = null,
        int priority = 100,
        TimeSpan? ttl = null,
        string? idempotencyKey = null,
        Guid? createdBy = null,
        CancellationToken ct = default);
}

/// <summary>Agent 指令服务（设计书 12 指令接口）</summary>
public interface IAgentCommandService
{
    Task<ClaimCommandsResponse> ClaimAsync(Guid clientId, ClaimCommandsRequest request, CancellationToken ct = default);
    Task ReportStartedAsync(Guid clientId, Guid commandId, CancellationToken ct = default);
    Task ReportProgressAsync(Guid clientId, Guid commandId, CommandProgressRequest request, CancellationToken ct = default);
    Task ReportCompletedAsync(Guid clientId, Guid commandId, CommandCompletedRequest request, CancellationToken ct = default);
}

/// <summary>指令服务实现</summary>
public class CommandService : ICommandDispatcher, IAgentCommandService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly CommandSigner _signer;
    private readonly IAlertingService _alerting;
    private readonly ILogger<CommandService> _logger;

    public CommandService(
        AppDbContext db,
        CommandSigner signer,
        IAlertingService alerting,
        ILogger<CommandService> logger)
    {
        _db = db;
        _signer = signer;
        _alerting = alerting;
        _logger = logger;
    }

    public async Task<Command> CreateCommandAsync(
        Guid clientId,
        CommandType commandType,
        Guid? taskId = null,
        Guid? candidateBackupSetId = null,
        object? payload = null,
        int priority = 100,
        TimeSpan? ttl = null,
        string? idempotencyKey = null,
        Guid? createdBy = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await _db.Commands
                .FirstOrDefaultAsync(c => c.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return existing;
        }

        var command = new Command
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateBackupSetId = candidateBackupSetId,
            CommandType = commandType,
            Status = CommandStatus.Pending,
            Priority = priority,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload),
            Nonce = TokenHasher.GenerateToken(16),
            CreatedBy = createdBy,
            ExpiresAt = DateTime.UtcNow.Add(ttl ?? DefaultTtl),
            IdempotencyKey = idempotencyKey
        };
        command.Signature = _signer.SignCommand(command);

        _db.Commands.Add(command);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "指令已下发 type={Type} clientId={ClientId} taskId={TaskId} commandId={CommandId}",
            command.CommandType, clientId, taskId, command.Id);

        return command;
    }

    public async Task<ClaimCommandsResponse> ClaimAsync(Guid clientId, ClaimCommandsRequest request, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new BusinessException("CLIENT_NOT_REGISTERED", "客户端未注册", 401);
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CLIENT_DISABLED", "客户端已禁用或已注销", 403);

        var supported = new List<CommandType>();
        foreach (var type in request.SupportedCommandTypes ?? [])
        {
            if (EnumMapping.TryParseSnakeCase<CommandType>(type, out var parsed))
                supported.Add(parsed);
        }

        var now = DateTime.UtcNow;
        var maxItems = Math.Clamp(request.MaxItems, 1, 50);

        var query = _db.Commands
            .Where(c => c.ClientId == clientId
                        && c.Status == CommandStatus.Pending
                        && c.ExpiresAt > now);
        if (supported.Count > 0)
            query = query.Where(c => supported.Contains(c.CommandType));

        var candidates = await query
            .OrderBy(c => c.Priority).ThenBy(c => c.CreatedAt)
            .Take(maxItems)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new ClaimCommandsResponse();

        // 原子认领：仅 pending 状态的指令被更新，杜绝并发重复认领
        var claimedCount = await _db.Commands
            .Where(c => candidates.Contains(c.Id) && c.Status == CommandStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CommandStatus.Claimed)
                .SetProperty(c => c.ClaimedAt, now), ct);

        var claimed = await _db.Commands
            .Where(c => candidates.Contains(c.Id) && c.Status == CommandStatus.Claimed && c.ClaimedAt == now)
            .OrderBy(c => c.Priority).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);

        _logger.LogInformation("客户端 {ClientId} 认领指令 {Count} 条", clientId, claimed.Count);

        return new ClaimCommandsResponse
        {
            Commands = claimed.Select(c => new CommandDto
            {
                Id = c.Id,
                Type = EnumMapping.ToSnakeCase(c.CommandType),
                TaskId = c.TaskId,
                CandidateBackupSetId = c.CandidateBackupSetId,
                Payload = c.Payload,
                CreatedAt = c.CreatedAt,
                ExpiresAt = c.ExpiresAt,
                Nonce = c.Nonce,
                Signature = c.Signature ?? _signer.SignCommand(c)
            }).ToList()
        };
    }

    public async Task ReportStartedAsync(Guid clientId, Guid commandId, CancellationToken ct = default)
    {
        var command = await LoadOwnedCommandAsync(clientId, commandId, ct);

        if (command.Status is not (CommandStatus.Claimed or CommandStatus.Running))
            throw new BusinessException("CONFLICT", $"指令当前状态 {EnumMapping.ToSnakeCase(command.Status)} 不允许上报开始", 409);

        if (command.Status == CommandStatus.Claimed)
        {
            command.Status = CommandStatus.Running;
            command.StartedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task ReportProgressAsync(Guid clientId, Guid commandId, CommandProgressRequest request, CancellationToken ct = default)
    {
        var command = await LoadOwnedCommandAsync(clientId, commandId, ct);

        if (command.Status is not (CommandStatus.Claimed or CommandStatus.Running))
            throw new BusinessException("CONFLICT", $"指令当前状态 {EnumMapping.ToSnakeCase(command.Status)} 不允许上报进度", 409);

        command.ResultPayload = JsonSerializer.Serialize(new
        {
            progress = new
            {
                request.Percent,
                request.Stage,
                request.Message,
                updatedAt = DateTime.UtcNow
            }
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReportCompletedAsync(Guid clientId, Guid commandId, CommandCompletedRequest request, CancellationToken ct = default)
    {
        var command = await LoadOwnedCommandAsync(clientId, commandId, ct);

        if (command.Status is CommandStatus.Succeeded or CommandStatus.Failed)
            return; // 幂等：重复上报直接返回

        if (command.Status is not (CommandStatus.Claimed or CommandStatus.Running))
            throw new BusinessException("CONFLICT", $"指令当前状态 {EnumMapping.ToSnakeCase(command.Status)} 不允许上报完成", 409);

        var now = DateTime.UtcNow;
        command.Status = request.Success ? CommandStatus.Succeeded : CommandStatus.Failed;
        command.CompletedAt = now;
        command.ResultCode = request.ResultCode;
        command.ResultMessage = request.ResultMessage;
        command.ResultPayload = request.Result;
        await _db.SaveChangesAsync(ct);

        await HandleCompletionSideEffectsAsync(command, ct);
    }

    /// <summary>指令完成副作用：批次计数、失败告警</summary>
    private async Task HandleCompletionSideEffectsAsync(Command command, CancellationToken ct)
    {
        // 批量上传批次计数
        if (!string.IsNullOrWhiteSpace(command.Payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(command.Payload);
                if (doc.RootElement.TryGetProperty("batchId", out var batchIdElement)
                    && batchIdElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(batchIdElement.GetString(), out var batchId))
                {
                    await UpdateBatchCountersAsync(batchId, command.Status == CommandStatus.Succeeded, ct);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "指令 {CommandId} payload 解析失败", command.Id);
            }
        }

        // 失败告警（预检/上传指令）
        if (command.Status == CommandStatus.Failed &&
            command.CommandType is CommandType.PrecheckTask or CommandType.PrecheckAll or CommandType.UploadCandidate)
        {
            var category = command.CommandType == CommandType.UploadCandidate ? "upload_failed" : "precheck_failed";
            await _alerting.RaiseAsync(
                $"command:{command.Id}:failed",
                AlertLevel.Warning,
                category,
                $"{EnumMapping.ToSnakeCase(command.CommandType)} 指令执行失败",
                command.ResultMessage ?? command.ResultCode,
                clientId: command.ClientId,
                taskId: command.TaskId,
                ct: ct);
        }
    }

    private async Task UpdateBatchCountersAsync(Guid batchId, bool succeeded, CancellationToken ct)
    {
        var batch = await _db.UploadBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null)
            return;

        if (succeeded)
            batch.SucceededItems++;
        else
            batch.FailedItems++;

        if (batch.Status == BatchStatus.Pending)
            batch.Status = BatchStatus.Running;

        if (batch.SucceededItems + batch.FailedItems >= batch.TotalItems)
        {
            batch.CompletedAt = DateTime.UtcNow;
            batch.Status = batch.FailedItems == 0
                ? BatchStatus.Completed
                : batch.SucceededItems == 0 ? BatchStatus.Failed : BatchStatus.Partial;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<Command> LoadOwnedCommandAsync(Guid clientId, Guid commandId, CancellationToken ct)
    {
        var command = await _db.Commands.FirstOrDefaultAsync(c => c.Id == commandId, ct)
            ?? throw new NotFoundException("指令", commandId);

        if (command.ClientId != clientId)
            throw new BusinessException("FORBIDDEN", "指令不属于当前客户端", 403);

        return command;
    }
}
