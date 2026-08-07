using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
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
                return AuthenticateResult.Fail("X-Client-Id 指向的客户端不存在");
            if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked or ClientStatus.PendingApproval)
                return AuthenticateResult.Fail("客户端状态不允许访问");

            Logger.LogWarning("使用开发回退头 X-Client-Id 完成客户端认证 clientId={ClientId}（生产环境必须关闭）", client.Id);
            return BuildTicket(client.Id, client.Hostname);
        }

        return AuthenticateResult.NoResult();
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
                return AuthenticateResult.Fail("客户端证书头来源不可信");
            }
            return null;
        }

        var verify = Context.Request.Headers[VerifyHeader].ToString();
        if (string.IsNullOrEmpty(verify) || verify.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return null; // 客户端未提供证书，交给后续回退分支

        if (!verify.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail($"代理侧客户端证书校验未通过：{verify}");

        var raw = Context.Request.Headers[FingerprintHeader].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return AuthenticateResult.Fail("代理未透传客户端证书指纹");

        // nginx $ssl_client_fingerprint 为无分隔小写十六进制；兼容带冒号的写法
        var normalized = raw.Replace(":", string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || !normalized.All(Uri.IsHexDigit))
            return AuthenticateResult.Fail("客户端证书指纹格式非法");

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
            return AuthenticateResult.Fail("未知的客户端证书");

        if (record.Status != CertificateStatus.Active)
            return AuthenticateResult.Fail("客户端证书已吊销或已过期");

        if (record.ExpiresAt < DateTime.UtcNow)
            return AuthenticateResult.Fail("客户端证书已过期");

        var client = record.Client;
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            return AuthenticateResult.Fail("客户端已禁用或已注销");
        if (client.Status == ClientStatus.PendingApproval)
            return AuthenticateResult.Fail("客户端尚未审批通过");

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
