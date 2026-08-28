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
    /// <param name="restartIfNotActive">
    /// 幂等键命中一条「已经不可能再被执行」的指令时（已成功完结，或还挂着 pending 但已过期），
    /// 把它复位成新的一条重新下发，而不是把旧结果原样返回。
    /// 给识别测试这类「同一个任务只该有一条在跑，但每次点都要真的重跑一遍」的动作用。
    /// </param>
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
        bool restartIfNotActive = false,
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

    /// <summary>result_code 列宽 64（审计 D-06）</summary>
    private const int MaxResultCodeLength = 64;

    /// <summary>result_message 列宽 2000，留 100 余量（与 UploadCommitWorker 对 ErrorMessage 同口径）</summary>
    private const int MaxResultMessageLength = 1900;

    /// <summary>
    /// result_payload 上限（UTF-8 字节）。browse_path 原先最多能返回 20000 个条目，
    /// 序列化几 MB 一份塞进 commands 表——Agent 自己在识别测试那条路径上写了
    /// 「会把 commands 表撑坏」的注释并限到 50 个文件，浏览这条却没设限。
    ///
    /// 取值 2MB 而不是更小：这是**兜底**上限，不是常规约束。真正管住体量的是
    /// RecognizerWizardService 那边把条目数封到 3000（一条目录条目序列化约 250 字节，
    /// 3000 条约 750KB，留了近三倍余量）。兜底值必须明显高于常规上限，
    /// 否则正常的浏览快照会被替换成摘要，识别向导直接解析不出结构——
    /// 那不是收口，是把一个撑表问题换成一个功能失效问题。
    /// </summary>
    private const int MaxResultPayloadBytes = 2 * 1024 * 1024;

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
        bool restartIfNotActive = false,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await _db.Commands
                .FirstOrDefaultAsync(c => c.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                var uploadAcceptedButCommitFailed = existing.Status == CommandStatus.Succeeded
                    && existing.CommandType is CommandType.UploadCandidate or CommandType.UploadLatest
                    && existing.CandidateBackupSetId is not null
                    && await _db.UploadSessions.AnyAsync(s =>
                        s.CandidateBackupSetId == existing.CandidateBackupSetId
                        && s.Status == UploadStatus.Failed, ct);

                // 幂等键命中一条不可能再被执行的指令：已经成功完结（结果是上一次的），
                // 或还挂着 pending 但 expires_at 已过（认领查询会跳过它，等于永远不会跑）。
                // 调用方声明了 restartIfNotActive 就复位重下，否则维持原样返回旧指令。
                var notActiveForRestart = restartIfNotActive
                    && (existing.Status == CommandStatus.Succeeded
                        || (existing.Status == CommandStatus.Pending && existing.ExpiresAt <= DateTime.UtcNow));

                if (existing.Status is CommandStatus.Failed or CommandStatus.Cancelled or CommandStatus.Expired or CommandStatus.Rejected
                    || uploadAcceptedButCommitFailed
                    || notActiveForRestart)
                {
                    existing.Status = CommandStatus.Pending;
                    existing.ClaimedAt = null;
                    existing.StartedAt = null;
                    existing.CompletedAt = null;
                    existing.ResultCode = null;
                    existing.ResultMessage = null;
                    existing.ResultPayload = null;
                    existing.ExpiresAt = DateTime.UtcNow.Add(ttl ?? DefaultTtl);
                    existing.Nonce = TokenHasher.GenerateToken(16);
                    existing.Signature = _signer.SignCommand(existing);
                    await _db.SaveChangesAsync(ct);
                }
                return existing;
            }
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
                // 每次下发都用当前签名算法重签，兼容从旧 HMAC 迁移到 RSA 的存量指令。
                Signature = _signer.SignCommand(c)
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

        // 审计 D-06：进度上报同样要收口。Message 来自客户端，长度不受任何约束，
        // 而 result_payload 是 jsonb——这里虽然是服务端自己序列化的，
        // 但内容仍是客户端给的，超大 Message 一样能把这张表撑坏。
        command.ResultPayload = NormalizeResultPayload(JsonSerializer.Serialize(new
        {
            progress = new
            {
                request.Percent,
                request.Stage,
                Message = Truncate(request.Message, MaxResultMessageLength),
                updatedAt = DateTime.UtcNow
            }
        }), commandId);
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

        // 审计 D-06：全部字段在服务端截断，不信客户端。
        // result_message 是 varchar(2000)，而 EF Core 的 HasMaxLength 只影响建表 DDL、
        // 运行时不截断。Agent 的失败回报走 ex.Message，一条带完整路径的 IOException
        // 轻易超过 2000 字节 → PostgreSQL 抛 22001 → 接口 500 → Agent 的 catch 分支
        // 再报一次、再 500 → 这条指令永远停在 running，正好接上 D-05。
        command.ResultCode = Truncate(request.ResultCode, MaxResultCodeLength);
        command.ResultMessage = Truncate(request.ResultMessage, MaxResultMessageLength);
        command.ResultPayload = NormalizeResultPayload(request.Result, commandId);
        await _db.SaveChangesAsync(ct);

        await HandleCompletionSideEffectsAsync(command, ct);
    }

    /// <summary>按列宽截断，null 原样返回（审计 D-06）</summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// result_payload 收口（审计 D-06）：先判大小再验形，两种情况都只降级不失败。
    ///
    /// 指令已经执行完了，坏掉的只是结果的呈现——为此把整条指令判失败，
    /// 会让「结果太大」表现成「备份失败」，是更糟的谎话。
    /// 非法 JSON 进 jsonb 列直接 500，同样会把 Agent 逼进「再报一次、再 500」的死循环。
    /// </summary>
    private string? NormalizeResultPayload(string? payload, Guid commandId)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        // 先按字节数判：Length 是 UTF-16 码元数，中文路径下和实际字节数差三倍，
        // 靠它判断会让「刚好没超」的载荷照样撑爆列。
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (byteCount > MaxResultPayloadBytes)
        {
            _logger.LogWarning(
                "指令 {CommandId} 的结果载荷 {Bytes} 字节超过上限 {Limit}，已替换为摘要",
                commandId, byteCount, MaxResultPayloadBytes);
            return JsonSerializer.Serialize(new
            {
                truncated = true,
                originalBytes = byteCount,
                limitBytes = MaxResultPayloadBytes
            });
        }

        try
        {
            using var _ = JsonDocument.Parse(payload);
            return payload;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "指令 {CommandId} 的结果载荷不是合法 JSON，已替换为摘要", commandId);
            return JsonSerializer.Serialize(new
            {
                invalidJson = true,
                preview = payload.Length <= 200 ? payload : payload[..200]
            });
        }
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
                $"{PlainText.Of(command.CommandType)}失败",
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
