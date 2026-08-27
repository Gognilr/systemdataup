using BackupMonitor.Core.Entities.Rbac;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 提示词 2（认证安全批次）回归测试：
/// - V005 管理员引导：注入式口令可登录；V017 之后不再强制首次改密（口令是安装时人工设的）
/// - 改密：旧口令校验失败不产生状态变更、成功后吊销全部刷新令牌并清除强制标志
/// - 口令强度服务端校验（长度 ≥ 12、不等于用户名）
/// - 不存在的用户登录走等成本 dummy 校验并返回统一错误（OPEN-ISSUES #7）
/// 每个用例使用独立测试用户，避免共享容器内的用例间耦合。
/// </summary>
[Collection("postgres")]
public class AuthSecurityRegressionTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public AuthSecurityRegressionTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // ---------- 组装：与生产相同的依赖图，数据库指向测试容器 ----------

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton<ICurrentContext, NullCurrentContext>();
        services.AddScoped<IAuditRecorder, DbAuditRecorder>();
        services.AddSingleton(new JwtTokenService(new JwtSettings
        {
            SigningKey = "unit-test-signing-key-0123456789abcdef0123456789",
            AccessTokenTtlSeconds = 3600,
            RefreshTokenTtlDays = 7
        }));
        services.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SystemSettingsProvider>.Instance));
        // 审计 H-14：撤权要即时生效，AuthService 现在会主动清这个缓存
        services.AddSingleton(sp => new TokenVersionCache(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TokenVersionCache>.Instance));
        return services.BuildServiceProvider();
    }

    private static AuthService CreateAuthService(IServiceScope scope)
    {
        var root = scope.ServiceProvider;
        return new AuthService(
            root.GetRequiredService<AppDbContext>(),
            root.GetRequiredService<JwtTokenService>(),
            root.GetRequiredService<SystemSettingsProvider>(),
            root.GetRequiredService<IAuditRecorder>(),
            root.GetRequiredService<TokenVersionCache>(),
            NullLogger<AuthService>.Instance);
    }

    private async Task<Guid> CreateTestUserAsync(IServiceScope scope, string username, string password, bool mustChange = false)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            Status = UserStatus.Active,
            PasswordChangedAt = DateTime.UtcNow,
            MustChangePassword = mustChange,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    // ---------- V005 引导 ----------

    /// <summary>
    /// 安装器设的管理员口令登录后可以直接用：口令是操作员在安装界面亲手输入的，
    /// 并且过了与 Web 端改密同一套 PasswordPolicy，首次登录再逼一次改密没有收益。
    /// V017 负责把 V005 打上的强制标志清掉。
    /// </summary>
    [Fact]
    public async Task V005_Admin_Bootstrap_Injects_Password_Without_Forcing_Change()
    {
        // 1. 数据库层：admin 存在且没有强制改密标志
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT must_change_password, status::text FROM users WHERE username='admin'", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "V005 应当种入 admin 账户");
        Assert.False(reader.GetBoolean(0));
        Assert.Equal("active", reader.GetString(1));
        await reader.CloseAsync();

        // 2. 服务层：注入的引导口令可以登录，且不再要求改密
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);

        var login = await auth.LoginAsync(
            new LoginRequest { Username = "admin", Password = PostgresDatabaseFixture.AdminBootstrapPassword },
            "127.0.0.1", "unit-test");

        Assert.False(login.MustChangePassword);
        Assert.False(string.IsNullOrEmpty(login.AccessToken));
        Assert.False(string.IsNullOrEmpty(login.RefreshToken));
    }

    [Fact]
    public async Task Login_Unknown_User_Returns_Same_Error_Without_State_Change()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);

        // OPEN-ISSUES #7：不存在的用户也执行 dummy bcrypt 校验，错误信息与密码错误一致
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            auth.LoginAsync(new LoginRequest { Username = "no_such_user_xyz", Password = "whatever-12345" },
                "127.0.0.1", "unit-test"));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("用户名或密码错误", ex.Message);
    }

    // ---------- 改密 ----------

    [Fact]
    public async Task ChangePassword_Wrong_Old_Password_Returns_401_And_Changes_Nothing()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);
        var userId = await CreateTestUserAsync(scope, "pw_wrongold", "Correct-Old-Pw-2026!", mustChange: true);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            auth.ChangePasswordAsync(userId, new ChangePasswordRequest
            {
                OldPassword = "Wrong-Old-Pw-9999!",
                NewPassword = "Brand-New-Pw-2026!!"
            }));

        Assert.Equal(401, ex.StatusCode);

        // 无任何状态变更：哈希、强制标志、刷新令牌均不变
        var after = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.True(after.MustChangePassword);
        Assert.Equal(0, await db.RefreshTokens.CountAsync(t => t.UserId == userId));

        // 审计记录了失败
        Assert.True(await db.AuditLogs.AnyAsync(a =>
            a.Action == "auth.change_password" && a.ResourceId == userId && a.Result == AuditResult.Failure));
    }

    [Fact]
    public async Task ChangePassword_Success_Clears_Flag_Revokes_All_Refresh_Tokens_And_Allows_New_Login()
    {
        const string oldPw = "Correct-Old-Pw-2026!";
        const string newPw = "Brand-New-Pw-2026!!";

        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);
        var userId = await CreateTestUserAsync(scope, "pw_success", oldPw, mustChange: true);

        // 先登录两次，制造两个有效刷新令牌
        await auth.LoginAsync(new LoginRequest { Username = "pw_success", Password = oldPw }, "127.0.0.1", "t");
        await auth.LoginAsync(new LoginRequest { Username = "pw_success", Password = oldPw }, "127.0.0.1", "t");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null));

        await auth.ChangePasswordAsync(userId, new ChangePasswordRequest { OldPassword = oldPw, NewPassword = newPw });

        // 验收口径：全部刷新令牌被吊销（reason=password_changed）
        Assert.Equal(0, await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null));
        Assert.Equal(2, await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokeReason == "password_changed"));

        // 强制标志清除、口令哈希已更新（bcrypt 成本 12）
        var after = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert.False(after.MustChangePassword);
        Assert.True(BCrypt.Net.BCrypt.Verify(newPw, after.PasswordHash));
        Assert.StartsWith("$2a$12$", after.PasswordHash);

        // 旧口令登录失败，新口令登录成功且不再强制改密
        await Assert.ThrowsAsync<BusinessException>(() =>
            auth.LoginAsync(new LoginRequest { Username = "pw_success", Password = oldPw }, "127.0.0.1", "t"));
        var relogin = await auth.LoginAsync(new LoginRequest { Username = "pw_success", Password = newPw }, "127.0.0.1", "t");
        Assert.False(relogin.MustChangePassword);
    }

    [Theory]
    [InlineData("short-1", "新口令长度不得少于 12 位")]
    [InlineData("@USERNAME@", "新口令不能与用户名相同")]
    public async Task ChangePassword_Strength_Rules_Are_Enforced_Server_Side(string newPwTemplate, string expectedMessage)
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);
        // Theory 每个用例共享容器，用户名必须唯一
        var username = $"pw_strength_{Guid.NewGuid():N}"[..20];
        var userId = await CreateTestUserAsync(scope, username, "Correct-Old-Pw-2026!");

        var newPw = newPwTemplate == "@USERNAME@" ? username : newPwTemplate;
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            auth.ChangePasswordAsync(userId, new ChangePasswordRequest { OldPassword = "Correct-Old-Pw-2026!", NewPassword = newPw }));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(expectedMessage, ex.Message);
    }

    /// <summary>
    /// 新口令与当前口令相同必须被拒绝，且不得清除强制改密标志。
    ///
    /// 这条不是形式主义：首次登录的强制改密如果接受「原样填回初始口令」，
    /// must_change_password 会被清掉而口令仍是安装器设的那个，
    /// 等于这道闸门自己给自己放行。
    /// </summary>
    [Fact]
    public async Task ChangePassword_Rejects_NewPassword_Identical_To_Current()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);
        const string password = "Correct-Old-Pw-2026!";
        var username = $"pw_same_{Guid.NewGuid():N}"[..20];
        var userId = await CreateTestUserAsync(scope, username, password, mustChange: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            auth.ChangePasswordAsync(userId, new ChangePasswordRequest
            {
                OldPassword = password,
                NewPassword = password
            }));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("新口令不能与当前口令相同", ex.Message);

        // 强制改密标志必须原样保留——否则闸门已经被绕过了
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert.True(user.MustChangePassword);
    }

    /// <summary>
    /// 审计 H-14：确认令牌泄露的那一刻，攻击者手上的访问令牌必须立刻失效。
    ///
    /// 原先重放检测只吊销刷新令牌——那是有状态的、立刻生效；
    /// 访问令牌是无状态 JWT，不动 TokenVersion 就得等它自然过期（最长一小时），
    /// 而这一小时正是最需要立刻切断的时候。改密和登出都做了这一步，
    /// 唯独安全性最敏感的这一条漏了。
    /// </summary>
    [Fact]
    public async Task 刷新令牌重放检测递增令牌版本并留下审计()
    {
        const string password = "Correct-Old-Pw-2026!";
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var username = $"reuse_{Guid.NewGuid():N}"[..20];
        var userId = await CreateTestUserAsync(scope, username, password);

        var login = await auth.LoginAsync(
            new LoginRequest { Username = username, Password = password }, "127.0.0.1", "t");
        var versionBefore = (await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId)).TokenVersion;

        // 正常轮换一次：旧令牌被标记吊销
        await auth.RefreshAsync(new RefreshTokenRequest { RefreshToken = login.RefreshToken }, "127.0.0.1", "t");

        // 再用同一个旧令牌——典型的泄露信号
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            auth.RefreshAsync(new RefreshTokenRequest { RefreshToken = login.RefreshToken }, "127.0.0.1", "t"));
        Assert.Equal(401, ex.StatusCode);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert.True(user.TokenVersion > versionBefore, "重放检测必须递增令牌版本，否则已签发的访问令牌仍然全权有效");

        // 缓存必须被清掉，否则新版本号最坏 60 秒后才被中间件看见
        var cache = scope.ServiceProvider.GetRequiredService<TokenVersionCache>();
        Assert.Equal(user.TokenVersion, await cache.GetCurrentVersionAsync(userId));

        // 令牌重放是最该留痕的安全事件之一，此前一条审计都没有
        Assert.True(await db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == "auth.token_reuse" && a.ResourceId == userId));

        // 该用户的全部刷新令牌都被吊销
        Assert.False(await db.RefreshTokens.AsNoTracking()
            .AnyAsync(t => t.UserId == userId && t.RevokedAt == null));
    }
}

/// <summary>测试用空上下文（无 HttpContext）</summary>
internal sealed class NullCurrentContext : ICurrentContext
{
    public Guid? UserId => null;
    public string? Username => null;
    public Guid? ClientId => null;
    public string? ClientIp => "127.0.0.1";
    public string? UserAgent => "unit-test";
    public string? RequestId => null;
}
