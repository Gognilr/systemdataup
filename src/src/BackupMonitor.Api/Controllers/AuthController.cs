using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Auth;
using BackupMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理员认证（设计书 9：登录/刷新/退出/当前用户）</summary>
[Route("api/v1/auth")]
public class AuthController : ApiBaseController
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>登录（9.1）</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var result = await _authService.LoginAsync(request, ip, userAgent, ct);
        return OkData(result);
    }

    /// <summary>刷新访问令牌（9.2，令牌轮换，旧令牌重用将吊销全部会话）</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<RefreshTokenResponse>>> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var result = await _authService.RefreshAsync(request, ip, userAgent, ct);
        return OkData(result);
    }

    /// <summary>退出登录（9.3，吊销刷新令牌；请求体可选，OPEN-ISSUES #9）</summary>
    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public async Task<ActionResult<ApiResponse>> Logout([FromBody] RefreshTokenRequest? request, CancellationToken ct)
    {
        await _authService.LogoutAsync(request?.RefreshToken, ct);
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
}
