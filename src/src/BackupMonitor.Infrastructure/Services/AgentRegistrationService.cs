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

            // 没有旧身份时这一项无意义（谈不上「连续」）；有旧身份时它决定第二态还是第三态。
            var identityProven = true;

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

                // 三态判定（A1）。核心是「能证明连续性就静默继承，不能证明就交给人确认」，
                // 而不是「不能证明就拒绝」——后者会把重装后永远 409 的死锁装回来。
                identityProven = VerifyContinuity(existing, request);

                await SupersedeOfflineClientAsync(existing, now, ct);
                superseded = existing;

                if (!identityProven)
                {
                    // 独立的审计动作，不复用注册成功那一条：这是事后唯一能回答
                    // 「那台机器是什么时候被换掉的」的地方。
                    await _audit.RecordAsync("agent.registration.identity_unproven", AuditResult.Failure,
                        "client", existing.Id,
                        afterData: JsonSerializer.Serialize(new
                        {
                            existing.MachineId,
                            previousHostname = existing.Hostname,
                            newHostname = request.Hostname,
                            hadProof = !string.IsNullOrWhiteSpace(request.ContinuityProof)
                        }), ct: ct);
                }
            }

            if (string.IsNullOrWhiteSpace(request.PublicKey) || !_certificateAuthority.CanParsePublicKey(request.PublicKey))
                throw new BusinessException("INVALID_REQUEST", "publicKey 缺失或不是合法的 Base64 SPKI 公钥", 400);

            var clientId = Guid.NewGuid();
            var issued = lanEnrollment && identityProven
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
                Status = lanEnrollment && identityProven ? ClientStatus.Online : ClientStatus.PendingApproval,
                EnrollmentMode = lanEnrollment ? "lan_simple" : "secure",
                ApprovedAt = lanEnrollment && identityProven ? now : null,
                // 第三态不签发证书：拿不到证书就上不了 mTLS，即使这一行被自动建出来，
                // 它在管理员点头之前也做不了任何事。
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

            if (superseded is not null && !identityProven)
            {
                // 审批界面上要看得见「为什么这一台要人点头」。不说清楚，审批就退化成盲点头
                // ——与 StructureInference 头部那条 Evidence 原则同源。
                client.Notes =
                    $"这台机器（{request.MachineId}）此前以另一把密钥注册过（旧身份 {superseded.Hostname}，"
                    + $"于 {now:yyyy-MM-dd HH:mm:ss} UTC 被本次注册取代），"
                    + "本次注册无法证明是同一台机器。确认是本机重装后再批准。";
            }

            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync("agent.registration", AuditResult.Success, "client", client.Id,
                afterData: JsonSerializer.Serialize(new
                {
                    client.MachineId,
                    client.Hostname,
                    mode = lanEnrollment ? "lan_simple" : "secure",
                    status = client.Status.ToString(),
                    identityProven
                }), ct: ct);

            if (lanEnrollment && identityProven && _alerting is not null)
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
                Status = lanEnrollment && identityProven ? "approved" : "pending_approval",
                PollAfterSeconds = lanEnrollment && identityProven ? 0 : 30
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
    ///
    /// 这里有一条必须写在纸面上的前提：<b>machine_id 是客户端自报的</b>，服务端无法验证它。
    /// 所以「让位」能力的信任来源在两种部署形态下完全不同，不要默认它一律受令牌保护：
    ///
    /// <list type="bullet">
    /// <item>Secure 模式：重新注册要出示一次性注册令牌，顶替能力随之受令牌管控。</item>
    /// <item>LanSimple + <c>LanMode:AutomaticEnrollment=true</c> 的免令牌形态：没有令牌这一关，
    /// 同网段任何人只要自报一台已被判定为 Offline 的机器的 machine_id，就能顶掉那条身份，
    /// 并继承它的显示名称与分组。这个形态的信任模型本来就是「信任这个网段」
    /// （另有 LanMode:PrivateNetworkOnly 和免令牌登记窗口两道闸），让位能力就落在这条信任线之内。</item>
    /// </list>
    ///
    /// 影响面到此为止：新身份拿的是<b>新的 clientId</b>，绑在旧 clientId 上的备份任务、历史备份、
    /// 证书一律不继承，被顶掉的旧身份是 Revoked 且证书已吊销；能跨过去的只有显示名称和分组
    /// 这两项纯展示属性。也就是说最坏情况是「网段内有人制造出一台名字眼熟的新客户端」，
    /// 不是「接管了旧客户端的数据」。
    /// </remarks>
    /// <summary>
    /// 这次注册能不能证明「我就是上一次那台机器」。
    ///
    /// 三条都满足才算证明成立：带了签名、带了旧公钥、旧公钥与库里存的那把逐字相同，
    /// 且签名用它验得过（签的内容是 machineId 本身）。
    ///
    /// 为什么要比对 PreviousPublicKey 而不是只用库里那把去验签：两者本该相同，
    /// 不同就说明请求方拿的是**另一把**旧密钥——那正是要识别的情况，
    /// 而只用库里那把去验会把它表现成「签名错误」，丢掉了「他手里有另一把钥匙」这条信息。
    ///
    /// 旧身份没存公钥（更早版本注册的）时无法验证，按「不能证明」处理走人工确认——
    /// 那正是这一条要的保守方向。
    /// </summary>
    private bool VerifyContinuity(Client existing, SubmitRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ContinuityProof)
            || string.IsNullOrWhiteSpace(request.PreviousPublicKey)
            || string.IsNullOrWhiteSpace(existing.PublicKey))
        {
            return false;
        }

        if (!string.Equals(existing.PublicKey.Trim(), request.PreviousPublicKey.Trim(), StringComparison.Ordinal))
            return false;

        try
        {
            var publicKey = Convert.FromBase64String(request.PreviousPublicKey.Trim());
            var signature = Convert.FromBase64String(request.ContinuityProof.Trim());
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return rsa.VerifyData(
                System.Text.Encoding.UTF8.GetBytes(request.MachineId),
                signature,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            // 签名或公钥格式不对：与验不过同等处理，交给人确认。
            _logger.LogWarning(ex, "身份连续性证明无法校验 machineId={MachineId}", request.MachineId);
            return false;
        }
    }

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
