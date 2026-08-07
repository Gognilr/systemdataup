using System.Text;
using System.Text.Json;
using BackupMonitor.Core.Entities.Rbac;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// OPEN-ISSUES #4 回归测试：登录/刷新链路的角色与权限加载由
/// 「两级集合 Include」改为「两条窄投影查询」之后，交付结果必须与原来完全一致：
/// 角色编码、权限编码完整且去重，访问令牌 claims 同步正确。
/// 若回退为投影缺失/条件错误的实现（例如忘记关联 RolePermissions），本测试应当失败。
/// </summary>
[Collection("postgres")]
public class AuthServiceProjectionTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public AuthServiceProjectionTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

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
            NullLogger<AuthService>.Instance);
    }

    /// <summary>解码 JWT 载荷段，返回指定 claim 的字符串值集合（单值/数组统一处理）。</summary>
    private static List<string> ReadJwtClaimValues(string jwt, string claimName)
    {
        var payload = jwt.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(claimName, out var element))
            return [];
        return element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [element.GetString()!];
    }

    /// <summary>
    /// 种子管理员（V005）持 system_admin 角色 → 16 项权限必须完整交付且无重复。
    /// </summary>
    [Fact]
    public async Task 登录_种子管理员_角色与全量权限完整交付()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);

        var login = await auth.LoginAsync(
            new LoginRequest { Username = "admin", Password = PostgresDatabaseFixture.AdminBootstrapPassword },
            "127.0.0.1", "unit-test");

        Assert.Equal(["system_admin"], login.User.Roles);
        Assert.Equal(16, login.User.Permissions.Count);
        Assert.Equal(16, login.User.Permissions.Distinct().Count());
        Assert.Contains("system.manage", login.User.Permissions);
        Assert.Contains("clients.read", login.User.Permissions);

        // 访问令牌 claims 与响应体一致
        var tokenPermissions = ReadJwtClaimValues(login.AccessToken, "permission");
        Assert.Equal(16, tokenPermissions.Distinct().Count());
        Assert.Equal(["system_admin"], ReadJwtClaimValues(login.AccessToken, "role"));
    }

    /// <summary>
    /// 多角色用户：两个角色权限部分重叠，交付的权限集合必须是并集且去重，
    /// 角色列表必须包含两个角色编码。
    /// </summary>
    [Fact]
    public async Task 登录_多角色用户_权限并集去重()
    {
        const string username = "proj_multi_role";
        const string password = "Proj-Multi-Role-Pw-2026!";

        await using var provider = BuildServices();

        // 组装：两个自定义角色，权限部分重叠（clients.read 重叠）
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var permClientsRead = await db.Permissions.SingleAsync(p => p.Code == "clients.read");
            var permTasksRead = await db.Permissions.SingleAsync(p => p.Code == "tasks.read");
            var permAuditRead = await db.Permissions.SingleAsync(p => p.Code == "audit.read");

            var roleA = new Role
            {
                Id = Guid.NewGuid(), Code = $"proj_role_a_{Guid.NewGuid():N}"[..40],
                Name = "投影测试角色A", IsSystem = false,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                RolePermissions =
                [
                    new RolePermission { RoleId = default, PermissionId = permClientsRead.Id },
                    new RolePermission { RoleId = default, PermissionId = permTasksRead.Id }
                ]
            };
            var roleB = new Role
            {
                Id = Guid.NewGuid(), Code = $"proj_role_b_{Guid.NewGuid():N}"[..40],
                Name = "投影测试角色B", IsSystem = false,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                RolePermissions =
                [
                    new RolePermission { RoleId = default, PermissionId = permClientsRead.Id },
                    new RolePermission { RoleId = default, PermissionId = permAuditRead.Id }
                ]
            };
            foreach (var rp in roleA.RolePermissions) rp.RoleId = roleA.Id;
            foreach (var rp in roleB.RolePermissions) rp.RoleId = roleB.Id;

            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                DisplayName = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
                Status = UserStatus.Active,
                PasswordChangedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Roles.AddRange(roleA, roleB);
            db.Users.Add(user);
            db.UserRoles.AddRange(
                new UserRole { UserId = user.Id, RoleId = roleA.Id, ScopeType = ScopeType.Global, CreatedAt = DateTime.UtcNow },
                new UserRole { UserId = user.Id, RoleId = roleB.Id, ScopeType = ScopeType.Global, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        // 验证：登录交付的权限为并集且无重复
        await using (var scope = provider.CreateAsyncScope())
        {
            var auth = CreateAuthService(scope);
            var login = await auth.LoginAsync(
                new LoginRequest { Username = username, Password = password },
                "127.0.0.1", "unit-test");

            Assert.Equal(2, login.User.Roles.Count);
            Assert.Equal(3, login.User.Permissions.Count);
            Assert.Equal(3, login.User.Permissions.Distinct().Count());
            Assert.Contains("clients.read", login.User.Permissions);
            Assert.Contains("tasks.read", login.User.Permissions);
            Assert.Contains("audit.read", login.User.Permissions);
        }
    }

    /// <summary>
    /// 刷新链路同样改为窄投影查询：刷新后新访问令牌的权限 claims 必须与登录时一致。
    /// </summary>
    [Fact]
    public async Task 刷新令牌_新访问令牌权限与登录一致()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var auth = CreateAuthService(scope);

        var login = await auth.LoginAsync(
            new LoginRequest { Username = "admin", Password = PostgresDatabaseFixture.AdminBootstrapPassword },
            "127.0.0.1", "unit-test");

        var refreshed = await auth.RefreshAsync(
            new RefreshTokenRequest { RefreshToken = login.RefreshToken },
            "127.0.0.1", "unit-test");

        var tokenPermissions = ReadJwtClaimValues(refreshed.AccessToken, "permission").Distinct().ToList();
        Assert.Equal(16, tokenPermissions.Count);
        Assert.Contains("system.manage", tokenPermissions);
        Assert.Equal(["system_admin"], ReadJwtClaimValues(refreshed.AccessToken, "role"));
    }
}
