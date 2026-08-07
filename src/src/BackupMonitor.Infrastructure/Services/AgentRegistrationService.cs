using System.Text.Json;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>客户端注册服务（设计书 10 客户端注册接口）</summary>
public interface IAgentRegistrationService
{
    Task<SubmitRegistrationResponse> SubmitAsync(SubmitRegistrationRequest request, CancellationToken ct = default);
    Task<RegistrationResultResponse> GetResultAsync(Guid registrationId, CancellationToken ct = default);
}

/// <summary>客户端注册实现</summary>
public class AgentRegistrationService : IAgentRegistrationService
{
    private readonly AppDbContext _db;
    private readonly CertificateAuthority _certificateAuthority;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<AgentRegistrationService> _logger;

    public AgentRegistrationService(
        AppDbContext db,
        CertificateAuthority certificateAuthority,
        IAuditRecorder audit,
        ILogger<AgentRegistrationService> logger)
    {
        _db = db;
        _certificateAuthority = certificateAuthority;
        _audit = audit;
        _logger = logger;
    }

    public async Task<SubmitRegistrationResponse> SubmitAsync(SubmitRegistrationRequest request, CancellationToken ct = default)
    {
        // 1. 注册令牌校验（只存哈希）
        var tokenHash = TokenHasher.Sha256Hex(request.RegistrationToken);
        var registrationToken = await _db.RegistrationTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

        var now = DateTime.UtcNow;
        if (registrationToken is null
            || registrationToken.Status != RegistrationTokenStatus.Active
            || (registrationToken.ExpiresAt is not null && registrationToken.ExpiresAt <= now)
            || registrationToken.UsedCount >= registrationToken.MaxUses)
        {
            await _audit.RecordAsync("agent.registration", AuditResult.Failure, "client", null,
                errorMessage: $"注册令牌无效 machineId={request.MachineId}", ct: ct);
            throw new BusinessException("UNAUTHORIZED", "注册令牌无效或已过期", 401);
        }

        // 2. 机器指纹唯一性
        if (await _db.Clients.AnyAsync(c => c.MachineId == request.MachineId, ct))
        {
            await _audit.RecordAsync("agent.registration", AuditResult.Failure, "client", null,
                errorMessage: $"机器指纹已注册 machineId={request.MachineId}", ct: ct);
            throw new BusinessException("CONFLICT", "该机器已注册，请勿重复提交", 409);
        }

        // 3. 公钥可解析性（审批签发证书依赖）
        if (string.IsNullOrWhiteSpace(request.PublicKey) || !_certificateAuthority.CanParsePublicKey(request.PublicKey))
        {
            throw new BusinessException("INVALID_REQUEST", "publicKey 缺失或不是合法的 Base64 SPKI 公钥", 400);
        }

        // 4. 创建待审批客户端
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = request.MachineId,
            Hostname = request.Hostname,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Hostname : request.DisplayName!,
            ClientGroupId = registrationToken.ClientGroupId,
            OsName = request.OsName,
            OsVersion = request.OsVersion,
            Architecture = request.Architecture,
            AgentVersion = request.AgentVersion,
            IpAddresses = request.IpAddresses is { Count: > 0 }
                ? JsonSerializer.Serialize(request.IpAddresses)
                : null,
            Status = ClientStatus.PendingApproval,
            PublicKey = request.PublicKey
        };

        _db.Clients.Add(client);

        registrationToken.UsedCount++;
        if (registrationToken.UsedCount >= registrationToken.MaxUses)
            registrationToken.Status = RegistrationTokenStatus.Expired;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("agent.registration", AuditResult.Success, "client", client.Id,
            afterData: JsonSerializer.Serialize(new { client.MachineId, client.Hostname }), ct: ct);

        _logger.LogInformation("客户端注册提交 hostname={Hostname} machineId={MachineId} clientId={ClientId}",
            client.Hostname, client.MachineId, client.Id);

        return new SubmitRegistrationResponse
        {
            RegistrationId = client.Id,
            Status = "pending_approval",
            PollAfterSeconds = 30
        };
    }

    public async Task<RegistrationResultResponse> GetResultAsync(Guid registrationId, CancellationToken ct = default)
    {
        var client = await _db.Clients
            .Include(c => c.Certificates)
            .FirstOrDefaultAsync(c => c.Id == registrationId, ct)
            ?? throw new NotFoundException("注册记录", registrationId);

        var response = new RegistrationResultResponse
        {
            RegistrationId = client.Id,
            ClientId = client.Id
        };

        switch (client.Status)
        {
            case ClientStatus.PendingApproval:
                response.Status = "pending_approval";
                response.PollAfterSeconds = 30;
                break;

            case ClientStatus.Disabled:
            case ClientStatus.Revoked:
                response.Status = "rejected";
                response.RejectionReason = client.Notes ?? "注册申请未通过";
                break;

            default:
                {
                    var certificate = client.Certificates
                        .Where(c => c.Status == CertificateStatus.Active)
                        .OrderByDescending(c => c.IssuedAt)
                        .FirstOrDefault();

                    if (certificate is null)
                    {
                        // 已审批但证书尚未签发（异常状态）
                        response.Status = "pending_approval";
                        response.PollAfterSeconds = 30;
                        break;
                    }

                    response.Status = "approved";
                    response.CertificatePem = certificate.CertificatePem;
                    response.CertificateThumbprint = certificate.Thumbprint;
                    response.CertificateExpiresAt = certificate.ExpiresAt;
                    break;
                }
        }

        return response;
    }
}
