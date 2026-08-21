using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;

namespace BackupMonitor.Api.Middleware;

/// <summary>
/// 强制改密闸门（OPEN-ISSUES #2 的服务端落地）。
///
/// 此前 must_change_password 只随登录响应交给浏览器，由前端 localStorage 决定是否跳转改密页，
/// 服务端不做任何校验——带初始口令登录拿到的就已经是全权限访问令牌，用 curl 直连接口，
/// 或在控制台清掉 localStorage 键，都能绕过"强制改密"。
///
/// 这里以 JWT 的 must_change_password claim 为唯一依据，在认证之后、授权之前拦截：
/// 未改密前只放行改密流程自身所需的少数端点，其余一律 403 PASSWORD_CHANGE_REQUIRED。
///
/// 只作用于管理端 JWT。Agent 走客户端证书方案，其主体不含此 claim，不受影响。
/// </summary>
public sealed class PasswordChangeRequiredMiddleware
{
    /// <summary>未改密前仍可访问的端点：改密自身，以及维持/结束会话所必需的。</summary>
    private static readonly string[] AllowedPaths =
    [
        "/api/v1/auth/change-password",
        "/api/v1/auth/me",
        "/api/v1/auth/login",
        "/api/v1/auth/refresh",
        "/api/v1/auth/logout"
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<PasswordChangeRequiredMiddleware> _logger;

    public PasswordChangeRequiredMiddleware(
        RequestDelegate next,
        ILogger<PasswordChangeRequiredMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresPasswordChange(context) && !IsAllowed(context.Request.Path))
        {
            _logger.LogWarning(
                "用户 {Username} 尚未完成强制改密，拒绝访问 {Method} {Path}",
                context.User.FindFirst(JwtClaimTypes.Username)?.Value ?? "unknown",
                context.Request.Method,
                context.Request.Path);

            throw new BusinessException(
                "PASSWORD_CHANGE_REQUIRED",
                "初始口令尚未修改，请先完成改密后再使用其他功能",
                403);
        }

        await _next(context);
    }

    private static bool RequiresPasswordChange(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && context.User.HasClaim(JwtClaimTypes.MustChangePassword, "true");

    private static bool IsAllowed(PathString path) =>
        AllowedPaths.Any(allowed =>
            path.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || path.Equals(allowed + "/", StringComparison.OrdinalIgnoreCase));
}
