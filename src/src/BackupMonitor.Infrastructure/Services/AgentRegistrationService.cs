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

            // 被这次注册顶掉的旧身份。留着它是为了把管理员在服务端设过的名字和分组
            // 继承到新行上——重装一台机器不该让「财务服务器」变回 WIN-8KJ2P3。
            Client? superseded = null;

            // 活跃/待审批实例不能被静默覆盖；旧的 revoked/disabled 身份可重新登记。
            var existing = await _db.Clients
                .Where(c => c.MachineId == request.MachineId)
                .OrderByDescending(c => c.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            if (existing is not null && existing.Status is not (ClientStatus.Revoked or ClientStatus.Disabled))
            {
                // 已被 SystemWatchdogWorker 判定掉线的旧身份让位给新注册。
                //
                // 这一条堵的是一个死锁：客户端重装后本地身份没了（卸载默认删除 state.json），
                // 新装的 Agent 只能重新提交注册，而服务端这边旧记录还在，于是永远 409。
                // Agent 卡在注册这一步，心跳循环根本不会启动，服务端看到的就是一台
                // 「有证书、从不心跳、疑似离线」的空壳，而且没有任何人工干预就永远好不了。
                //
                // 让位只认 Offline：那是服务端自己按 client_offline_threshold_seconds
                // 确认过的静默，不是客户端一句话。Online / SuspectedOffline 一律拒绝——
                // 疑似离线只是漏了一次心跳，机器多半还活着，让它被一个自称同 machineId
                // 的请求顶掉，就等于把「不能静默覆盖活跃实例」这条给废了。
                if (existing.Status != ClientStatus.Offline)
                {
                    await _audit.RecordAsync("agent.registration", AuditResult.Failure, "client", null,
                        errorMessage: $"机器指纹已注册 machineId={request.MachineId}", ct: ct);
                    await transaction.CommitAsync(ct);
                    committed = true;
                    throw new BusinessException("CONFLICT", "该机器已注册，请勿重复提交", 409);
                }

                await SupersedeOfflineClientAsync(existing, now, ct);
                superseded = existing;
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
                DisplayName = ResolveDisplayName(request, superseded),
                // 令牌带的分组是这次登记的明确意图，优先；没带才继承旧身份的分组。
                ClientGroupId = registrationToken?.ClientGroupId ?? superseded?.ClientGroupId,
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

    /// <summary>
    /// 决定重新登记后这台机器在界面上叫什么。
    /// </summary>
    /// <remarks>
    /// 优先级是「Agent 端特意配的名字 &gt; 管理员在服务端改过的名字 &gt; 主机名」。
    ///
    /// 判断「有没有人特意起过名字」两处都不能只看字段有没有值：
    /// Agent 没配 DisplayName 时会把主机名填进去（AgentWorker 提交注册请求处），
    /// 服务端的 DisplayName 在注册时也是主机名的拷贝。所以两处都以「与 Hostname 不同」
    /// 作为「被人工命名过」的判据。
    ///
    /// 中间那一档是这次改动的要点：管理员起的「财务服务器」必须活过一次卸载重装，
    /// 否则服务端改名在重装面前形同虚设。而没被改过的名字绝不继承——
    /// 机器改名后重装，界面上还挂着旧主机名，比什么都不做更让人困惑。
    /// </remarks>
    private static string ResolveDisplayName(SubmitRegistrationRequest request, Client? superseded)
    {
        var requested = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Hostname : request.DisplayName!;
        if (!string.Equals(requested, request.Hostname, StringComparison.OrdinalIgnoreCase))
            return requested;

        if (superseded is not null
            && !string.Equals(superseded.DisplayName, superseded.Hostname, StringComparison.OrdinalIgnoreCase))
            return superseded.DisplayName;

        return requested;
    }

    /// <summary>
    /// 把已确认掉线的旧身份就地注销，给同一 machine_id 的新注册让出位置。
    /// </summary>
    /// <remarks>
    /// 必须单独 SaveChanges 一次，不能和新客户端的 INSERT 挤在同一批：
    /// uq_clients_machine_id_active 是 <c>WHERE status NOT IN ('revoked','disabled')</c>
    /// 的部分唯一索引，即时校验，而 EF 不保证 UPDATE 一定排在 INSERT 前面。
    /// 调用方已经开了事务，这次中间提交仍然是原子的。
    /// </remarks>
    private async Task SupersedeOfflineClientAsync(Client existing, DateTime now, CancellationToken ct)
    {
        existing.Status = ClientStatus.Revoked;
        existing.Notes = $"已被同一台机器的重新登记取代（{now:yyyy-MM-dd HH:mm:ss} UTC）";

        // 旧证书一并吊销：新身份签发之后，旧证书再能通过 mTLS 就是一条无主的入口。
        var activeCertificates = await _db.ClientCertificates
            .Where(c => c.ClientId == existing.Id && c.Status == CertificateStatus.Active)
            .ToListAsync(ct);
        foreach (var certificate in activeCertificates)
        {
            certificate.Status = CertificateStatus.Revoked;
            certificate.RevokedAt = now;
            certificate.RevokeReason = "重新登记取代旧身份";
        }

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("client.superseded", AuditResult.Success, "client", existing.Id,
            afterData: JsonSerializer.Serialize(new
            {
                status = "revoked",
                reason = "reenrollment",
                existing.MachineId,
                revokedCertificates = activeCertificates.Count
            }), ct: ct);

        _logger.LogInformation(
            "旧客户端身份 {ClientId}({Hostname}) 已确认掉线，被同一 machineId 的重新登记取代，吊销证书 {Count} 张",
            existing.Id, existing.Hostname, activeCertificates.Count);
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
