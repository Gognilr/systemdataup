using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BackupMonitor.Api.Middleware;
using BackupMonitor.Core.Entities.Rbac;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 强制改密闸门的服务端回归测试（OPEN-ISSUES #2）。
///
/// 修复前 must_change_password 只经登录响应交给浏览器，由前端 localStorage 决定是否跳转，
/// 服务端零校验——带初始口令登录拿到的就是全权限令牌，curl 直连或清掉 localStorage 即可绕过。
/// 这里锁死两件事：① 该状态必须进入 JWT；② 中间件必须据此拦截业务端点。
///
/// 全部为纯内存用例，不依赖 PostgreSQL 容器。
/// </summary>
public class PasswordChangeGateTests
{
    private static readonly JwtSettings Settings = new()
    {
        SigningKey = "test-signing-key-at-least-32-bytes-long!",
        Issuer = "BackupMonitor",
        Audience = "BackupMonitor",
        AccessTokenTtlSeconds = 3600
    };

    private static User MakeUser(bool mustChangePassword) => new()
    {
        Id = Guid.NewGuid(),
        Username = "admin",
        DisplayName = "系统管理员",
        PasswordHash = "irrelevant",
        Status = UserStatus.Active,
        MustChangePassword = mustChangePassword
    };

    private static IEnumerable<Claim> ClaimsOf(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims;

    // ---------- 令牌侧：状态必须随令牌下发 ----------

    [Fact]
    public void 未改密用户的访问令牌必须携带_must_change_password_claim()
    {
        var token = new JwtTokenService(Settings)
            .CreateAccessToken(MakeUser(mustChangePassword: true), ["admin"], ["system.manage"]);

        Assert.Contains(ClaimsOf(token),
            c => c.Type == JwtClaimTypes.MustChangePassword && c.Value == "true");
    }

    [Fact]
    public void 已改密用户的访问令牌不得携带该_claim()
    {
        var token = new JwtTokenService(Settings)
            .CreateAccessToken(MakeUser(mustChangePassword: false), ["admin"], ["system.manage"]);

        Assert.DoesNotContain(ClaimsOf(token), c => c.Type == JwtClaimTypes.MustChangePassword);
    }

    // ---------- 闸门侧：据此拦截 ----------

    private static HttpContext MakeContext(string path, bool authenticated, bool mustChangePassword)
    {
        var claims = new List<Claim> { new(JwtClaimTypes.Username, "admin") };
        if (mustChangePassword)
            claims.Add(new Claim(JwtClaimTypes.MustChangePassword, "true"));

        // authenticationType 非空才会让 Identity.IsAuthenticated 为 true
        var identity = authenticated ? new ClaimsIdentity(claims, "Bearer") : new ClaimsIdentity(claims);

        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        context.Request.Path = path;
        context.Request.Method = HttpMethods.Get;
        return context;
    }

    private static async Task<bool> PassesAsync(HttpContext context)
    {
        var reached = false;
        var middleware = new PasswordChangeRequiredMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            NullLogger<PasswordChangeRequiredMiddleware>.Instance);

        await middleware.InvokeAsync(context);
        return reached;
    }

    [Theory]
    [InlineData("/api/v1/admin/clients")]
    [InlineData("/api/v1/admin/backups")]
    [InlineData("/api/v1/admin/restore-requests")]
    [InlineData("/api/v1/admin/retention-policies")]
    [InlineData("/health/db")]
    public async Task 未改密时业务端点一律拒绝(string path)
    {
        var context = MakeContext(path, authenticated: true, mustChangePassword: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => PassesAsync(context));

        Assert.Equal("PASSWORD_CHANGE_REQUIRED", ex.ErrorCode);
        Assert.Equal(403, ex.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/auth/change-password")]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/auth/logout")]
    public async Task 未改密时改密流程自身必须放行(string path)
    {
        var context = MakeContext(path, authenticated: true, mustChangePassword: true);

        Assert.True(await PassesAsync(context), $"{path} 属于改密流程，必须放行");
    }

    [Fact]
    public async Task 路径匹配不区分大小写且容忍尾随斜杠()
    {
        Assert.True(await PassesAsync(
            MakeContext("/API/V1/Auth/Change-Password", authenticated: true, mustChangePassword: true)));
        Assert.True(await PassesAsync(
            MakeContext("/api/v1/auth/change-password/", authenticated: true, mustChangePassword: true)));
    }

    [Fact]
    public async Task 已改密用户不受影响()
    {
        var context = MakeContext("/api/v1/admin/clients", authenticated: true, mustChangePassword: false);

        Assert.True(await PassesAsync(context));
    }

    [Fact]
    public async Task Agent_等未携带该_claim_的主体不受影响()
    {
        // Agent 走客户端证书方案，其主体不含 must_change_password，闸门必须完全透明
        var context = MakeContext("/api/v1/agent/heartbeat", authenticated: false, mustChangePassword: false);

        Assert.True(await PassesAsync(context));
    }
}
