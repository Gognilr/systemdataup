using System.IdentityModel.Tokens.Jwt;
using BackupMonitor.Infrastructure.Security;

namespace BackupMonitor.Api.Middleware;

/// <summary>
/// 令牌版本校验（整改批次 C · C4 的服务端落地）。
///
/// 管理端 access token 里带 tv claim（JwtTokenService.CreateAccessToken 签发），
/// 与库中 users.token_version（TokenVersionCache，60 秒缓存）比对：
/// 改密、登出、（未来实现的）禁用账号/改角色/改权限都会让 TokenVersion++，
/// 不一致直接 401，最坏 60 秒后已撤销的令牌就作废，不必等到自然过期。
///
/// 位于认证之后（需要 tv claim）、授权之前。只处理带 tv claim 的管理端 JWT；
/// Agent 走客户端证书方案，其主体不含此 claim，不受影响。
/// </summary>
public sealed class TokenVersionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TokenVersionCache _cache;
    private readonly ILogger<TokenVersionMiddleware> _logger;

    public TokenVersionMiddleware(RequestDelegate next, TokenVersionCache cache, ILogger<TokenVersionMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tvClaim = context.User.FindFirst(JwtClaimTypes.TokenVersion)?.Value;
            if (tvClaim is not null && int.TryParse(tvClaim, out var tokenVersion))
            {
                var subClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                if (Guid.TryParse(subClaim, out var userId))
                {
                    var currentVersion = await _cache.GetCurrentVersionAsync(userId, context.RequestAborted);
                    if (currentVersion != TokenVersionCache.SkipCheck && currentVersion != tokenVersion)
                    {
                        _logger.LogWarning(
                            "用户 {UserId} 的令牌版本号已过期（token={TokenVersion}, current={CurrentVersion}），拒绝访问 {Method} {Path}",
                            userId, tokenVersion, currentVersion, context.Request.Method, context.Request.Path);

                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync(
                            """{"success":false,"error":{"code":"TOKEN_REVOKED","message":"登录状态已失效，请重新登录"}}""",
                            context.RequestAborted);
                        return;
                    }
                }
            }
        }

        await _next(context);
    }
}
