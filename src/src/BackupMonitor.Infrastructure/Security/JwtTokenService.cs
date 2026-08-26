using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BackupMonitor.Core.Entities.Rbac;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace BackupMonitor.Infrastructure.Security;

/// <summary>JWT 配置（appsettings: Security:Jwt）</summary>
public class JwtSettings
{
    public const string SectionName = "Security:Jwt";

    /// <summary>HS256 签名密钥（生产环境必须替换为强密钥）</summary>
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "BackupMonitor";
    public string Audience { get; set; } = "BackupMonitor";

    /// <summary>访问令牌有效期（秒）</summary>
    public int AccessTokenTtlSeconds { get; set; } = 3600;

    /// <summary>刷新令牌有效期（天）</summary>
    public int RefreshTokenTtlDays { get; set; } = 7;

    public SymmetricSecurityKey CreateSecurityKey() =>
        new(Encoding.UTF8.GetBytes(SigningKey));

    public TokenValidationParameters CreateValidationParameters() =>
        new()
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = CreateSecurityKey(),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
}

/// <summary>JWT 常量</summary>
public static class JwtClaimTypes
{
    public const string DisplayName = "display_name";
    public const string Username = "username";
    public const string Role = "role";
    public const string Permission = "permission";

    /// <summary>
    /// 未完成首次强制改密的标记（OPEN-ISSUES #2）。
    /// 只在 users.must_change_password 为 true 时写入，值恒为 "true"；
    /// 由 PasswordChangeRequiredMiddleware 在服务端强制执行。
    /// </summary>
    public const string MustChangePassword = "must_change_password";

    /// <summary>客户端证书认证方案写入的客户端 ID claim</summary>
    public const string ClientId = "client_id";

    /// <summary>客户端证书认证方案写入的主机名 claim</summary>
    public const string ClientHostname = "client_hostname";

    /// <summary>
    /// 整改批次 C · C4：令牌版本号。签发时写入 users.token_version 的快照，
    /// TokenVersionMiddleware 拿它跟库中当前值比对（60 秒缓存），不一致即视为令牌已被撤销。
    /// </summary>
    public const string TokenVersion = "tv";
}

/// <summary>管理员访问令牌签发服务</summary>
public class JwtTokenService
{
    private readonly JwtSettings _settings;

    public JwtTokenService(JwtSettings settings)
    {
        _settings = settings;
    }

    public int AccessTokenTtlSeconds => _settings.AccessTokenTtlSeconds;
    public int RefreshTokenTtlDays => _settings.RefreshTokenTtlDays;

    /// <summary>为已认证用户签发访问令牌</summary>
    public string CreateAccessToken(User user, IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(JwtClaimTypes.Username, user.Username),
            new(JwtClaimTypes.DisplayName, user.DisplayName),
            new(JwtClaimTypes.TokenVersion, user.TokenVersion.ToString(), ClaimValueTypes.Integer32)
        };

        // 强制改密状态必须随令牌下发，否则服务端无从判断（OPEN-ISSUES #2）。
        // 改密成功后 users.must_change_password 置 false，下一次签发的令牌自然不再带此 claim；
        // 同时 ChangePasswordAsync 会吊销全部刷新令牌，旧令牌最迟在自然过期后失效。
        if (user.MustChangePassword)
            claims.Add(new Claim(JwtClaimTypes.MustChangePassword, "true"));

        foreach (var role in roles.Distinct())
            claims.Add(new Claim(JwtClaimTypes.Role, role));

        foreach (var permission in permissions.Distinct())
            claims.Add(new Claim(JwtClaimTypes.Permission, permission));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddSeconds(_settings.AccessTokenTtlSeconds),
            SigningCredentials = new SigningCredentials(
                _settings.CreateSecurityKey(), SecurityAlgorithms.HmacSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }
}
