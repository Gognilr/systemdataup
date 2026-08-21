using System.Text.Json;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>Agent 升级服务（第二批补充设计：经 upgrade_agent 指令通道下发，无独立数据表）</summary>
public interface IAgentUpgradeService
{
    /// <summary>向指定客户端批量下发升级指令（逐项返回结果，设计书 2.2）</summary>
    Task<UpgradeAgentResponseDto> DispatchAsync(UpgradeAgentRequestDto request, string? idempotencyKey, CancellationToken ct = default);
}

/// <summary>Agent 升级实现</summary>
public class AgentUpgradeService : IAgentUpgradeService
{
    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<AgentUpgradeService> _logger;

    public AgentUpgradeService(
        AppDbContext db,
        ICommandDispatcher dispatcher,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<AgentUpgradeService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task<UpgradeAgentResponseDto> DispatchAsync(UpgradeAgentRequestDto request, string? idempotencyKey, CancellationToken ct = default)
    {
        if (request.ClientIds.Count == 0)
            throw new ValidationFailedException("clientIds 至少需要一台客户端");
        if (string.IsNullOrWhiteSpace(request.TargetVersion))
            throw new ValidationFailedException("targetVersion 必填");
        if (string.IsNullOrWhiteSpace(request.PackageUrl))
            throw new ValidationFailedException("packageUrl 必填");
        if (!Uri.TryCreate(request.PackageUrl.Trim(), UriKind.Absolute, out var packageUri)
            || (!string.Equals(packageUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(packageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationFailedException("packageUrl 必须是 HTTP 或 HTTPS 地址");
        }
        if (string.IsNullOrWhiteSpace(request.PackageSha256)
            || !System.Text.RegularExpressions.Regex.IsMatch(request.PackageSha256, "^[0-9a-fA-F]{64}$"))
        {
            throw new ValidationFailedException("packageSha256 必须为 64 位十六进制");
        }

        var response = new UpgradeAgentResponseDto();
        var userId = _context.UserId;

        foreach (var clientId in request.ClientIds.Distinct())
        {
            var item = new UpgradeClientResultDto { ClientId = clientId };

            try
            {
                var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
                if (client is null)
                {
                    item.ErrorCode = "CLIENT_NOT_REGISTERED";
                    item.ErrorMessage = "客户端不存在";
                    response.Failed++;
                    response.Items.Add(item);
                    continue;
                }

                item.Hostname = client.Hostname;

                if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
                {
                    item.ErrorCode = "CLIENT_DISABLED";
                    item.ErrorMessage = $"客户端状态为 {EnumMapping.ToSnakeCase(client.Status)}，不能下发升级";
                    response.Failed++;
                    response.Items.Add(item);
                    continue;
                }

                var payload = new
                {
                    targetVersion = request.TargetVersion.Trim(),
                    packageUrl = packageUri.ToString(),
                    sha256 = request.PackageSha256.ToLowerInvariant(),
                    note = request.Note
                };

                // 幂等键按客户端派生，保证逐客户端幂等（设计书 8.5 下发指令需幂等）
                var derivedKey = string.IsNullOrWhiteSpace(idempotencyKey)
                    ? null
                    : $"upgrade:{idempotencyKey}:{clientId}";

                var command = await _dispatcher.CreateCommandAsync(
                    clientId,
                    CommandType.UpgradeAgent,
                    payload: payload,
                    idempotencyKey: derivedKey,
                    createdBy: userId,
                    ct: ct);

                item.Success = true;
                item.CommandId = command.Id;
                response.Dispatched++;
            }
            catch (BusinessException ex)
            {
                item.ErrorCode = ex.ErrorCode;
                item.ErrorMessage = ex.Message;
                response.Failed++;
            }

            response.Items.Add(item);
        }

        await _audit.RecordAsync("agent.upgrade_dispatch", AuditResult.Success, "command", null,
            afterData: JsonSerializer.Serialize(new
            {
                clientCount = response.Items.Count,
                response.Dispatched,
                response.Failed,
                request.TargetVersion,
                request.PackageUrl
            }), ct: ct);

        _logger.LogInformation("Agent 升级下发完成：成功 {Dispatched}，失败 {Failed}，目标版本 {Version}",
            response.Dispatched, response.Failed, request.TargetVersion);

        return response;
    }
}
