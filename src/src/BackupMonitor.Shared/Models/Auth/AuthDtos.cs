using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Auth;

/// <summary>管理员登录请求（设计书 9.1）</summary>
public class LoginRequest
{
    [Required]
    public string Username { get; set; } = null!;

    [Required]
    public string Password { get; set; } = null!;
}

/// <summary>登录响应（设计书 9.1）</summary>
public class LoginResponse
{
    public string AccessToken { get; set; } = null!;
    public string RefreshToken { get; set; } = null!;

    /// <summary>访问令牌有效期（秒）</summary>
    public int ExpiresIn { get; set; }

    public CurrentUserDto User { get; set; } = null!;

    /// <summary>是否必须立即修改口令（仅当口令由系统代设时为 true；安装器设的初始管理员口令不置位，见 V017）</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// 本次签发的刷新令牌实际有效天数（审查 P2-4）。
    /// Cookie 的 MaxAge 必须取这个值——它和写进 refresh_tokens.expires_at 的是同一个来源
    /// （system_settings.refresh_token_ttl_days，缺省回落到配置）。原先 Cookie 取配置、
    /// 令牌取数据库设置，两者不一致时用户要么提前掉线，要么揣着一张早已失效的 Cookie。
    /// </summary>
    public int RefreshTokenTtlDays { get; set; }
}

/// <summary>当前登录用户信息</summary>
public class CurrentUserDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string? Email { get; set; }
    public List<string> Roles { get; set; } = [];
    public List<string> Permissions { get; set; } = [];

    /// <summary>
    /// 是否仍需完成强制改密（OPEN-ISSUES #2）。
    /// 由 /auth/me 一并返回，让前端刷新后无需依赖 localStorage 也能还原改密拦截状态。
    /// </summary>
    public bool MustChangePassword { get; set; }
}

/// <summary>刷新令牌请求（设计书 9.2）</summary>
public class RefreshTokenRequest
{
    [Required]
    public string RefreshToken { get; set; } = null!;
}

/// <summary>刷新令牌响应</summary>
public class RefreshTokenResponse
{
    public string AccessToken { get; set; } = null!;
    public string RefreshToken { get; set; } = null!;
    public int ExpiresIn { get; set; }

    /// <summary>本次签发的刷新令牌实际有效天数，Cookie MaxAge 取此值（审查 P2-4）</summary>
    public int RefreshTokenTtlDays { get; set; }
}

/// <summary>修改口令请求（OPEN-ISSUES #2，需 Bearer 登录态）</summary>
public class ChangePasswordRequest
{
    [Required]
    public string OldPassword { get; set; } = null!;

    [Required]
    public string NewPassword { get; set; } = null!;
}
