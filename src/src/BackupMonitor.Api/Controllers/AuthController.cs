using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Auth;
using BackupMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理员认证（设计书 9：登录/刷新/退出/当前用户）</summary>
[Route("api/v1/auth")]
public class AuthController : ApiBaseController
{
    private const string RefreshCookieName = "__Host-backupmonitor-refresh";

    /// <summary>凭据类端点的 IP 级限速策略名（审查 P2-2），策略本体在 Program.cs 注册。</summary>
    public const string RateLimitPolicy = "auth-ip";

    private readonly IAuthService _authService;
    private readonly JwtSettings _jwtSettings;

    public AuthController(IAuthService authService, JwtSettings jwtSettings)
    {
        _authService = authService;
        _jwtSettings = jwtSettings;
    }

    /// <summary>登录（9.1）</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var result = await _authService.LoginAsync(request, ip, userAgent, ct);
        SetRefreshCookie(result.RefreshToken, result.RefreshTokenTtlDays);
        result.RefreshToken = string.Empty;
        return OkData(result);
    }

    /// <summary>刷新访问令牌（9.2，令牌轮换，旧令牌重用将吊销全部会话）</summary>
    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimitPolicy)]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<RefreshTokenResponse>>> Refresh(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshTokenRequest? request,
        CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var refreshToken = Request.Cookies[RefreshCookieName] ?? request?.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new BusinessException("UNAUTHORIZED", "刷新令牌缺失", 401);
        var result = await _authService.RefreshAsync(new RefreshTokenRequest { RefreshToken = refreshToken }, ip, userAgent, ct);
        SetRefreshCookie(result.RefreshToken, result.RefreshTokenTtlDays);
        result.RefreshToken = string.Empty;
        return OkData(result);
    }

    /// <summary>退出登录（9.3，吊销刷新令牌；请求体可选，OPEN-ISSUES #9）</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse>> Logout(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshTokenRequest? request,
        CancellationToken ct)
    {
        await _authService.LogoutAsync(Request.Cookies[RefreshCookieName] ?? request?.RefreshToken, ct);
        Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
        return OkMessage("已退出登录");
    }

    /// <summary>修改当前用户口令（OPEN-ISSUES #2：校验旧口令，成功后吊销全部刷新令牌）</summary>
    [HttpPost("change-password")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public async Task<ActionResult<ApiResponse>> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = UserId
            ?? throw new BusinessException("UNAUTHORIZED", "无法识别当前用户", 401);
        await _authService.ChangePasswordAsync(userId, request, ct);
        return OkMessage("口令修改成功，请使用新口令重新登录");
    }

    /// <summary>当前用户信息（9.4）</summary>
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public async Task<ActionResult<ApiResponse<CurrentUserDto>>> Me(CancellationToken ct)
    {
        var userId = UserId
            ?? throw new BusinessException("UNAUTHORIZED", "无法识别当前用户", 401);
        var result = await _authService.GetCurrentUserAsync(userId, ct);
        return OkData(result);
    }

    /// <summary>
    /// 写刷新令牌 Cookie。ttlDays 必须来自签发方（审查 P2-4）——
    /// 它和写进 refresh_tokens.expires_at 的是同一个来源，不能各自去读配置。
    /// </summary>
    private void SetRefreshCookie(string refreshToken, int ttlDays)
    {
        Response.Cookies.Append(RefreshCookieName, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(Math.Max(1, ttlDays > 0 ? ttlDays : _jwtSettings.RefreshTokenTtlDays))
        });
    }
}
