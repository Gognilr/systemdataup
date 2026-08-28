using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BackupMonitor.Api.Middleware;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Api.Security;

public class ClientCertificateAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// 客户端证书（mTLS）认证方案（设计书 23.1）：
/// 以证书指纹查 client_certificates，校验证书状态与客户端状态，
/// 成功后写入 client_id / client_hostname claim。
///
/// 支持两种取证书的方式：
/// 1) Kestrel 直接终结 mTLS —— 取 Connection.ClientCertificate；
/// 2) nginx 终结 mTLS 后反向代理 —— 取 X-Client-Verify / X-Client-Fingerprint 头。
///    第 2 种仅在 Security:ClientAuth:TrustForwardedCertificate=true
///    且 TCP 直连对端属于 Security:ClientAuth:ForwardedProxies 时采信，
///    否则任何人都能伪造头部冒充任意客户端。
///
/// 开发环境可配置 Security:ClientAuth:AllowDevelopmentHeader=true 用 X-Client-Id 头替代证书。
/// </summary>
public class ClientCertificateAuthenticationHandler : AuthenticationHandler<ClientCertificateAuthenticationOptions>
{
    public const string SchemeName = "ClientCertificate";

    /// <summary>
    /// 401 响应体里的失败原因码（方案 B）。
    ///
    /// 原先所有认证失败都被 ExceptionHandlingMiddleware 统一成 UNAUTHORIZED，
    /// Agent 拿到的信息只有「401」三个字符，于是它唯一能做的就是无限重试。
    /// 「服务端不认识这张证书」和「管理员把这台机器禁了」在客户端看来完全一样，
    /// 而这两件事要求的处置完全相反：前者应当重新登记，后者绝对不能——
    /// 否则管理员刚禁用的机器自己就转回来了。
    /// </summary>
    /// <remarks>
    /// 字面量本身在 <see cref="AgentAuthFailureCodes"/>（Shared）里，Agent 侧引用的是同一份。
    /// 这里保留一层转发只是为了不改动本文件里既有的调用点。
    /// </remarks>
    public static class FailureCodes
    {
        /// <summary>这条连接压根没出示客户端证书。多半是连接复用了注册阶段的匿名连接，重新握手即可。</summary>
        public const string CertificateMissing = AgentAuthFailureCodes.CertificateMissing;

        /// <summary>指纹查不到记录：服务端换过空库，本机身份在服务端已不存在。可以重新登记。</summary>
        public const string IdentityUnknown = AgentAuthFailureCodes.IdentityUnknown;

        /// <summary>证书存在但已吊销/作废。身份还在，重新登记解决不了问题。</summary>
        public const string CertificateRevoked = AgentAuthFailureCodes.CertificateRevoked;

        /// <summary>证书已过期。续签路径的事，不是重新登记的事。</summary>
        public const string CertificateExpired = AgentAuthFailureCodes.CertificateExpired;

        /// <summary>客户端被管理员禁用或注销。客户端收到它必须安静退让，不得重新登记。</summary>
        public const string ClientDisabled = AgentAuthFailureCodes.ClientDisabled;

        /// <summary>客户端还在等审批。</summary>
        public const string ClientPendingApproval = AgentAuthFailureCodes.ClientPendingApproval;

        /// <summary>代理透传的证书头不可信或格式非法，与客户端身份无关。</summary>
        public const string ForwardedRejected = AgentAuthFailureCodes.ForwardedRejected;
    }

    /// <summary>
    /// 本次认证失败原因码在 HttpContext.Items 里的键。
    ///
    /// 认证失败到写响应体之间隔着框架的 Challenge 流程，中间没有别的通道能把
    /// 原因带出去：AuthenticateResult.Fail 只有一个 message，而 message 是给人看的，
    /// 不能让 Agent 去匹配中文字符串来决定要不要重新登记。
    /// </summary>
    private const string FailureCodeItemKey = "__ClientAuthFailureCode";

    /// <summary>失败原因的人可读说明，与 <see cref="FailureCodeItemKey"/> 成对写入。</summary>
    private const string FailureMessageItemKey = "__ClientAuthFailureMessage";

    private static readonly System.Text.Json.JsonSerializerOptions ErrorJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// TCP 直连对端 IP 的 HttpContext.Items 键。
    /// UseForwardedHeaders 会把 RemoteIpAddress 改写成真实客户端 IP，
    /// 而「本请求是否来自可信代理」必须基于改写前的直连对端判断，
    /// 因此由 Program.cs 中位于 UseForwardedHeaders 之前的中间件预先存入。
    /// </summary>
    public const string PeerAddressItemKey = "__PeerAddress";

    /// <summary>nginx: proxy_set_header X-Client-Verify $ssl_client_verify;（SUCCESS / FAILED:… / NONE）</summary>
    private const string VerifyHeader = "X-Client-Verify";

    /// <summary>nginx: proxy_set_header X-Client-Fingerprint $ssl_client_fingerprint;（SHA-1 十六进制）</summary>
    private const string FingerprintHeader = "X-Client-Fingerprint";

    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public ClientCertificateAuthenticationHandler(
        IOptionsMonitor<ClientCertificateAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppDbContext db,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _db = db;
        _configuration = configuration;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1) Kestrel 直接终结 mTLS
        var certificate = Context.Connection.ClientCertificate;
        if (certificate is not null)
            return await AuthenticateByThumbprintAsync(certificate.Thumbprint);

        // 2) nginx 终结 mTLS，经可信代理透传
        if (_configuration.GetValue("Security:ClientAuth:TrustForwardedCertificate", false))
        {
            var forwarded = TryAuthenticateForwarded(out var thumbprint);
            if (forwarded is not null)
                return forwarded;
            if (thumbprint is not null)
                return await AuthenticateByThumbprintAsync(thumbprint);
        }

        // 3) 开发回退：HTTP 调试时以请求头替代证书（生产必须关闭）
        if (_configuration.GetValue("Security:ClientAuth:AllowDevelopmentHeader", false)
            && Context.Request.Headers.TryGetValue("X-Client-Id", out var headerValue)
            && Guid.TryParse(headerValue.ToString(), out var headerClientId))
        {
            var client = await _db.Clients.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == headerClientId);

            if (client is null)
                return Fail(FailureCodes.IdentityUnknown, "X-Client-Id 指向的客户端不存在");
            if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked or ClientStatus.PendingApproval)
                return Fail(FailureCodes.ClientDisabled, "客户端状态不允许访问");

            Logger.LogWarning("使用开发回退头 X-Client-Id 完成客户端认证 clientId={ClientId}（生产环境必须关闭）", client.Id);
            return BuildTicket(client.Id, client.Hostname);
        }

        // 走到这里说明这条连接根本没有出示客户端证书。
        //
        // 这是 mTLS 形态下最难查的一种失败：Agent 侧只看到 401，服务端默认什么都不记，
        // 两边都没有线索，只能靠猜。而服务端其实掌握着最关键的事实——TLS 握手阶段
        // 对方到底给没给证书。把它记下来，排障就不必再依赖客户机器上的日志。
        Logger.LogWarning(
            "客户端认证失败：连接未出示客户端证书 peer={Peer} path={Path}。"
            + "常见原因：Agent 尚未获批（没有证书）、证书文件损坏，或连接复用了注册阶段建立的匿名连接。",
            PeerAddress()?.ToString() ?? "unknown",
            Context.Request.Path.Value);

        // 仍然返回 NoResult（这条链路上还可能挂着别的认证方案），但把原因码留下：
        // 「没出示证书」对 Agent 意味着重新握手，绝不是「身份没了、该重新登记」。
        StashFailure(FailureCodes.CertificateMissing, "连接未出示客户端证书");
        return AuthenticateResult.NoResult();
    }

    /// <summary>
    /// 记下失败原因码再返回 Fail。原因码要跨到 <see cref="HandleChallengeAsync"/> 才用得上，
    /// 中间隔着框架的挑战流程，只能借 HttpContext.Items 传。
    /// </summary>
    private AuthenticateResult Fail(string code, string message)
    {
        StashFailure(code, message);
        return AuthenticateResult.Fail(message);
    }

    private void StashFailure(string code, string message)
    {
        Context.Items[FailureCodeItemKey] = code;
        Context.Items[FailureMessageItemKey] = message;
    }

    /// <summary>
    /// 401 的响应体在这里写，而不是留给 ExceptionHandlingMiddleware 的兜底分支。
    ///
    /// 兜底分支只看得到状态码，写出来的永远是 UNAUTHORIZED——本方案要区分的原因
    /// 恰好就丢在那一步。形状与全局错误响应完全一致（ApiResponse + code/message），
    /// 客户端不需要为这条路径单开解析分支。
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted)
            return;

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json; charset=utf-8";

        var code = Context.Items.TryGetValue(FailureCodeItemKey, out var storedCode) && storedCode is string codeValue
            ? codeValue
            : "UNAUTHORIZED";
        var message = Context.Items.TryGetValue(FailureMessageItemKey, out var storedMessage) && storedMessage is string messageValue
            ? messageValue
            : "未认证或认证已过期";
        var requestId = Context.Items.TryGetValue(RequestIdMiddleware.ContextItemKey, out var rid)
            ? rid?.ToString()
            : null;

        var body = new ApiResponse<object>
        {
            Success = false,
            RequestId = requestId,
            Error = new ApiError { Code = code, Message = message }
        };

        var payload = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(body, ErrorJsonOptions));

        // 显式写 ContentLength，不只是为了合规：ExceptionHandlingMiddleware 在管线返回时
        // 会给「状态码是 401/403 但没有响应体」的请求补一个笼统的 UNAUTHORIZED，
        // 它的判据正是 HasStarted 与 ContentLength。小响应体是否已经冲刷出去
        // 取决于服务器的缓冲行为，不该让本方案要区分的失败码依赖那个时序——
        // 一旦被补写覆盖，Agent 看到的又是那个什么都没说的 UNAUTHORIZED，
        // 身份自愈和「被禁用不得重新登记」两条判断会一起失效。
        Response.ContentLength = payload.Length;
        await Response.Body.WriteAsync(payload);
    }

    /// <summary>
    /// 校验代理透传的证书头。三种结果：
    /// 返回非 null —— 终局失败；
    /// 返回 null 且 thumbprint 非 null —— 校验通过，可继续按指纹查库；
    /// 返回 null 且 thumbprint 为 null —— 未提供证书，继续走后续回退分支。
    /// </summary>
    private AuthenticateResult? TryAuthenticateForwarded(out string? thumbprint)
    {
        thumbprint = null;

        if (!IsFromTrustedProxy())
        {
            // 头部存在却不是来自可信代理 —— 典型的伪造尝试，留痕
            if (Context.Request.Headers.ContainsKey(FingerprintHeader))
            {
                Logger.LogWarning(
                    "拒绝非可信来源 {Peer} 提交的 {Header} 头（疑似伪造客户端证书）",
                    PeerAddress()?.ToString() ?? "unknown", FingerprintHeader);
                return Fail(FailureCodes.ForwardedRejected, "客户端证书头来源不可信");
            }
            return null;
        }

        var verify = Context.Request.Headers[VerifyHeader].ToString();
        if (string.IsNullOrEmpty(verify) || verify.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return null; // 客户端未提供证书，交给后续回退分支

        if (!verify.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
            return Fail(FailureCodes.ForwardedRejected, $"代理侧客户端证书校验未通过：{verify}");

        var raw = Context.Request.Headers[FingerprintHeader].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return Fail(FailureCodes.ForwardedRejected, "代理未透传客户端证书指纹");

        // nginx $ssl_client_fingerprint 为无分隔小写十六进制；兼容带冒号的写法
        var normalized = raw.Replace(":", string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || !normalized.All(Uri.IsHexDigit))
            return Fail(FailureCodes.ForwardedRejected, "客户端证书指纹格式非法");

        thumbprint = normalized;
        return null;
    }

    /// <summary>按证书指纹查库并校验证书与客户端状态</summary>
    private async Task<AuthenticateResult> AuthenticateByThumbprintAsync(string rawThumbprint)
    {
        var thumbprint = rawThumbprint.Replace(":", string.Empty).Trim().ToLowerInvariant();

        var record = await _db.ClientCertificates.AsNoTracking()
            .Include(c => c.Client)
            .FirstOrDefaultAsync(c => c.Thumbprint == thumbprint);

        if (record is null)
        {
            Logger.LogWarning(
                "客户端认证失败：证书指纹 {Thumbprint} 不在 client_certificates 中 peer={Peer}。"
                + "常见原因：数据库被重建过而 Agent 仍持有旧证书——需要在客户端重新注册。",
                thumbprint,
                PeerAddress()?.ToString() ?? "unknown");
            return Fail(FailureCodes.IdentityUnknown, "未知的客户端证书");
        }

        var supersededOverlap = record.Status == CertificateStatus.Superseded
            && record.RevokedAt is not null
            && record.RevokedAt.Value.AddDays(7) > DateTime.UtcNow;
        if (record.Status != CertificateStatus.Active && !supersededOverlap)
        {
            Logger.LogWarning(
                "客户端认证失败：证书 {Thumbprint} 状态为 {Status}（clientId={ClientId}）",
                thumbprint, record.Status, record.ClientId);
            return Fail(FailureCodes.CertificateRevoked, "客户端证书已吊销或已过期");
        }

        if (record.ExpiresAt < DateTime.UtcNow)
        {
            Logger.LogWarning(
                "客户端认证失败：证书 {Thumbprint} 已于 {ExpiresAt:O} 过期（clientId={ClientId}）",
                thumbprint, record.ExpiresAt, record.ClientId);
            return Fail(FailureCodes.CertificateExpired, "客户端证书已过期");
        }

        var client = record.Client;
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
        {
            Logger.LogWarning(
                "客户端认证失败：客户端 {ClientId}({Hostname}) 状态为 {Status}",
                client.Id, client.Hostname, client.Status);
            return Fail(FailureCodes.ClientDisabled, "客户端已禁用或已注销");
        }

        if (client.Status == ClientStatus.PendingApproval)
        {
            Logger.LogWarning(
                "客户端认证失败：客户端 {ClientId}({Hostname}) 尚未审批通过",
                client.Id, client.Hostname);
            return Fail(FailureCodes.ClientPendingApproval, "客户端尚未审批通过");
        }

        return BuildTicket(client.Id, client.Hostname);
    }

    /// <summary>TCP 直连对端是否为配置中的可信反向代理</summary>
    private bool IsFromTrustedProxy()
    {
        var peer = PeerAddress();
        if (peer is null)
            return false;

        var trusted = _configuration
            .GetSection("Security:ClientAuth:ForwardedProxies")
            .Get<string[]>();

        if (trusted is null || trusted.Length == 0)
            return false;

        foreach (var entry in trusted)
        {
            if (IPAddress.TryParse(entry, out var ip) && ip.Equals(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer))
                return true;
        }

        return false;
    }

    private IPAddress? PeerAddress() =>
        Context.Items.TryGetValue(PeerAddressItemKey, out var stored) && stored is IPAddress addr
            ? addr
            : Context.Connection.RemoteIpAddress;

    private AuthenticateResult BuildTicket(Guid clientId, string hostname)
    {
        var claims = new[]
        {
            new Claim(JwtClaimTypes.ClientId, clientId.ToString()),
            new Claim(JwtClaimTypes.ClientHostname, hostname)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
