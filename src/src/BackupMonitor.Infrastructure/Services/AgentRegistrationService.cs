using System.Net;
using System.Text.Json;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Configuration;
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
    private readonly ICurrentContext _context;
    private readonly IConfiguration _configuration;
    private readonly LanEnrollmentService _enrollment;
    private readonly IAlertingService? _alerting;
    private readonly ILogger<AgentRegistrationService> _logger;

    // 保留旧的直接构造方式，便于不使用 DI 的测试和宿主平滑升级。
    public AgentRegistrationService(
        AppDbContext db,
        CertificateAuthority certificateAuthority,
        IAuditRecorder audit,
        ICurrentContext context,
        IConfiguration configuration,
        ILogger<AgentRegistrationService> logger)
        : this(db, certificateAuthority, audit, context, configuration, new LanEnrollmentService(db), logger, null)
    {
    }

    public AgentRegistrationService(
        AppDbContext db,
        CertificateAuthority certificateAuthority,
        IAuditRecorder audit,
        ICurrentContext context,
        IConfiguration configuration,
        LanEnrollmentService enrollment,
        ILogger<AgentRegistrationService> logger,
        IAlertingService? alerting = null)
    {
        _db = db;
        _certificateAuthority = certificateAuthority;
        _audit = audit;
        _context = context;
        _configuration = configuration;
        _enrollment = enrollment;
        _alerting = alerting;
        _logger = logger;
    }

    public async Task<SubmitRegistrationResponse> SubmitAsync(SubmitRegistrationRequest request, CancellationToken ct = default)
    {
        var automaticEnrollment = IsAutomaticEnrollmentEnabled();
        var privateNetworkRequest = IsPrivateNetworkRequest();
        var hasToken = !string.IsNullOrWhiteSpace(request.RegistrationToken);
        var lanEnrollment = automaticEnrollment && privateNetworkRequest && !hasToken;

        if (lanEnrollment)
        {
            var configuredUntil = ParseEnrollmentOpenUntil();
            var openUntil = await _enrollment.GetOpenUntilAsync(configuredUntil, ct);
            if (openUntil is null || openUntil <= DateTime.UtcNow)
            {
                await _audit.RecordAsync("agent.registration", AuditResult.Failure, "client", null,
                    errorCode: "ENROLLMENT_WINDOW_CLOSED", errorMessage: "LAN 自动登记窗口已关闭", ct: ct);
                throw new BusinessException("UNAUTHORIZED", "LAN 自动登记窗口已关闭，请联系管理员临时开放登记", 401);
            }
        }

        if (!hasToken && !lanEnrollment)
        {
            await _audit.RecordAsync("agent.registration", AuditResult.Failure, "client", null,
                errorCode: "UNAUTHORIZED", errorMessage: "缺少注册令牌且当前请求不满足 LAN 自动登记策略", ct: ct);
            throw new BusinessException(
                "UNAUTHORIZED",
                automaticEnrollment
                    ? "LAN 自动登记只允许来自私有网络或本机的请求"
                    : "注册令牌无效或已过期",
                401);
        }

        // Secure 注册令牌的使用次数必须和客户端创建放在同一事务，并对令牌行加锁；
        // 否则并发注册会同时通过 UsedCount 检查，超出 MaxUses。
        var tokenHash = hasToken ? TokenHasher.Sha256Hex(request.RegistrationToken!) : null;
        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var committed = false;
        try
        {
            var registrationToken = hasToken
                ? await _db.RegistrationTokens
                    .FromSqlInterpolated($"SELECT * FROM registration_tokens WHERE token_hash = {tokenHash} FOR UPDATE")
                    .FirstOrDefaultAsync(ct)
                : null;

            if (hasToken && (registrationToken is null
                || registrationToken.Status != RegistrationTokenStatus.Active
                || registrationToken.ExpiresAt is not null && registrationToken.ExpiresAt <= now
                || registrationToken.UsedCount >= registrationToken.MaxUses))
            {
                await _audit.RecordAsync("agent.registration", AuditResult.Failure, "client", null,
                    errorMessage: $"注册令牌无效 machineId={request.MachineId}", ct: ct);
                await transaction.CommitAsync(ct);
                committed = true;
                throw new BusinessException("UNAUTHORIZED", "注册令牌无效或已过期", 401);
            }

            // 活跃/待审批实例不能被静默覆盖；旧的 revoked/disabled 身份可重新登记。
            var existing = await _db.Clients
                .Where(c => c.MachineId == request.MachineId)
                .OrderByDescending(c => c.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            if (existing is not null && existing.Status is not (ClientStatus.Revoked or ClientStatus.Disabled))
            {
                await _audit.RecordAsync("agent.registration", AuditResult.Failure, "client", null,
                    errorMessage: $"机器指纹已注册 machineId={request.MachineId}", ct: ct);
                await transaction.CommitAsync(ct);
                committed = true;
                throw new BusinessException("CONFLICT", "该机器已注册，请勿重复提交", 409);
            }

            if (string.IsNullOrWhiteSpace(request.PublicKey) || !_certificateAuthority.CanParsePublicKey(request.PublicKey))
                throw new BusinessException("INVALID_REQUEST", "publicKey 缺失或不是合法的 Base64 SPKI 公钥", 400);

            var clientId = Guid.NewGuid();
            var issued = lanEnrollment
                ? await _certificateAuthority.IssueClientCertificateAsync(clientId, request.Hostname, request.PublicKey)
                : null;

            var client = new Client
            {
                Id = clientId,
                MachineId = request.MachineId,
                Hostname = request.Hostname,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Hostname : request.DisplayName!,
                ClientGroupId = registrationToken?.ClientGroupId,
                OsName = request.OsName,
                OsVersion = request.OsVersion,
                Architecture = request.Architecture,
                AgentVersion = request.AgentVersion,
                IpAddresses = request.IpAddresses is { Count: > 0 }
                    ? JsonSerializer.Serialize(request.IpAddresses)
                    : null,
                Status = lanEnrollment ? ClientStatus.Online : ClientStatus.PendingApproval,
                EnrollmentMode = lanEnrollment ? "lan_simple" : "secure",
                ApprovedAt = lanEnrollment ? now : null,
                CertificateThumbprint = issued?.Thumbprint,
                CertificateExpiresAt = issued?.ExpiresAt,
                PublicKey = request.PublicKey
            };

            _db.Clients.Add(client);

            if (issued is not null)
            {
                _db.ClientCertificates.Add(new ClientCertificate
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    Thumbprint = issued.Thumbprint,
                    SerialNumber = issued.SerialNumber,
                    IssuedAt = issued.IssuedAt,
                    ExpiresAt = issued.ExpiresAt,
                    Status = CertificateStatus.Active,
                    CertificatePem = issued.CertificatePem
                });
            }

            if (registrationToken is not null)
            {
                registrationToken.UsedCount++;
                if (registrationToken.UsedCount >= registrationToken.MaxUses)
                    registrationToken.Status = RegistrationTokenStatus.Expired;
            }

            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync("agent.registration", AuditResult.Success, "client", client.Id,
                afterData: JsonSerializer.Serialize(new
                {
                    client.MachineId,
                    client.Hostname,
                    mode = lanEnrollment ? "lan_simple" : "secure",
                    status = client.Status.ToString()
                }), ct: ct);

            if (lanEnrollment && _alerting is not null)
            {
                await _alerting.RaiseAsync(
                    $"client:{client.Id}:automatic-enrollment",
                    AlertLevel.Notice,
                    "client_enrollment",
                    "客户端自动登记成功",
                    $"客户端 {client.Hostname} 已通过局域网自动登记并签发客户端证书。",
                    clientId: client.Id,
                    metadata: JsonSerializer.Serialize(new
                    {
                        enrollmentMode = "lan_simple",
                        enrolledAtUtc = now
                    }),
                    ct: ct);
            }

            await transaction.CommitAsync(ct);
            committed = true;

            _logger.LogInformation("客户端注册提交 hostname={Hostname} machineId={MachineId} clientId={ClientId}",
                client.Hostname, client.MachineId, client.Id);

            return new SubmitRegistrationResponse
            {
                RegistrationId = client.Id,
                Status = lanEnrollment ? "approved" : "pending_approval",
                PollAfterSeconds = lanEnrollment ? 0 : 30
            };
        }
        catch (DbUpdateException ex) when (IsMachineIdConflict(ex))
        {
            if (!committed)
                await transaction.RollbackAsync(CancellationToken.None);
            throw new BusinessException("CONFLICT", "该机器正在并发登记或已有活动身份，请稍后重试", 409);
        }
        catch
        {
            if (!committed)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private bool IsAutomaticEnrollmentEnabled() =>
        string.Equals(_configuration.GetValue<string>("DeploymentMode") ?? "Secure", "LanSimple", StringComparison.OrdinalIgnoreCase)
        && _configuration.GetValue("LanMode:AutomaticEnrollment", false);

    private bool IsPrivateNetworkRequest()
    {
        if (!_configuration.GetValue("LanMode:PrivateNetworkOnly", true))
            return true;

        return IPAddress.TryParse(_context.ClientIp, out var address)
            && LanNetworkPolicy.IsPrivateOrLoopback(address);
    }

    private DateTime? ParseEnrollmentOpenUntil()
    {
        var value = _configuration["LanMode:EnrollmentOpenUntil"];
        return DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static bool IsMachineIdConflict(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains("uq_clients_machine_id", StringComparison.OrdinalIgnoreCase) == true
        || exception.InnerException?.Message.Contains("machine_id", StringComparison.OrdinalIgnoreCase) == true;

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
