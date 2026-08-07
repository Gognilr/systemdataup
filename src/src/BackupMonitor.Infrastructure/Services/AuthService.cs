using BackupMonitor.Core.Entities.Rbac;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>管理员认证服务（设计书 9 认证接口 / 23.2 管理认证）</summary>
public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, string? clientIp, string? userAgent, CancellationToken ct = default);
    Task<RefreshTokenResponse> RefreshAsync(RefreshTokenRequest request, string? clientIp, string? userAgent, CancellationToken ct = default);
    Task LogoutAsync(string? refreshToken, CancellationToken ct = default);
    Task<CurrentUserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>管理员认证实现（bcrypt 密码校验 + JWT + 可吊销刷新令牌轮换）</summary>
public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly SystemSettingsProvider _settings;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        JwtTokenService jwt,
        SystemSettingsProvider settings,
        IAuditRecorder audit,
        ILogger<AuthService> logger)
    {
        _db = db;
        _jwt = jwt;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, string? clientIp, string? userAgent, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Username == request.Username, ct);

        // 统一错误信息，不暴露用户是否存在
        if (user is null)
        {
            await _audit.RecordAsync("auth.login", AuditResult.Failure, "user", null,
                errorMessage: $"用户 {request.Username} 登录失败（用户不存在）", ct: ct);
            throw new BusinessException("UNAUTHORIZED", "用户名或密码错误", 401);
        }

        var now = DateTime.UtcNow;

        if (user.Status == UserStatus.Disabled)
            throw new BusinessException("FORBIDDEN", "账号已停用，请联系系统管理员", 403);

        if (user.LockedUntil is not null && user.LockedUntil > now)
        {
            await _audit.RecordAsync("auth.login", AuditResult.Failure, "user", user.Id,
                errorMessage: "账号处于锁定状态", ct: ct);
            throw new BusinessException("UNAUTHORIZED", "登录失败次数过多，账号已临时锁定，请稍后重试", 401);
        }

        bool passwordOk;
        try
        {
            passwordOk = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "密码哈希校验异常 user={Username}", user.Username);
            passwordOk = false;
        }

        if (!passwordOk)
        {
            user.FailedLoginCount++;
            var maxAttempts = await _settings.GetIntAsync("max_login_attempts", 5, ct);
            if (user.FailedLoginCount >= maxAttempts)
            {
                var lockSeconds = await _settings.GetIntAsync("lockout_duration_seconds", 900, ct);
                user.LockedUntil = now.AddSeconds(lockSeconds);
                user.FailedLoginCount = 0;
            }
            await _db.SaveChangesAsync(ct);

            await _audit.RecordAsync("auth.login", AuditResult.Failure, "user", user.Id,
                errorMessage: "密码错误", ct: ct);
            throw new BusinessException("UNAUTHORIZED", "用户名或密码错误", 401);
        }

        if (user.Status != UserStatus.Active)
            throw new BusinessException("FORBIDDEN", "账号状态异常，请联系系统管理员", 403);

        // 登录成功
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;

        var roles = user.UserRoles.Select(ur => ur.Role.Code).Distinct().ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

        var accessToken = _jwt.CreateAccessToken(user, roles, permissions);
        var refreshToken = await CreateRefreshTokenAsync(user, clientIp, userAgent, ct);

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("auth.login", AuditResult.Success, "user", user.Id, ct: ct);

        return new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresIn = _jwt.AccessTokenTtlSeconds,
            User = new CurrentUserDto
            {
                Id = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Roles = roles,
                Permissions = permissions
            }
        };
    }

    public async Task<RefreshTokenResponse> RefreshAsync(RefreshTokenRequest request, string? clientIp, string? userAgent, CancellationToken ct = default)
    {
        var tokenHash = TokenHasher.Sha256Hex(request.RefreshToken);

        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            ?? throw new BusinessException("UNAUTHORIZED", "刷新令牌无效", 401);

        var now = DateTime.UtcNow;

        if (stored.RevokedAt is not null)
        {
            // 被吊销的令牌再次使用：可能是令牌泄露，吊销该用户全部刷新令牌
            _logger.LogWarning("检测到已吊销刷新令牌重用 user={UserId}，吊销该用户全部刷新令牌", stored.UserId);
            await RevokeAllUserTokensAsync(stored.UserId, "token_reuse", ct);
            throw new BusinessException("UNAUTHORIZED", "刷新令牌已失效，请重新登录", 401);
        }

        if (stored.ExpiresAt <= now)
            throw new BusinessException("UNAUTHORIZED", "刷新令牌已过期，请重新登录", 401);

        if (stored.User.Status != UserStatus.Active)
            throw new BusinessException("FORBIDDEN", "账号状态异常，无法刷新令牌", 403);

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstAsync(u => u.Id == stored.UserId, ct);

        // 轮换：旧令牌标记吊销，签发新令牌
        var newToken = await CreateRefreshTokenAsync(user, clientIp, userAgent, ct);
        stored.RevokedAt = now;
        stored.RevokeReason = "rotated";
        stored.ReplacedByHash = TokenHasher.Sha256Hex(newToken);

        var roles = user.UserRoles.Select(ur => ur.Role.Code).Distinct().ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

        await _db.SaveChangesAsync(ct);

        return new RefreshTokenResponse
        {
            AccessToken = _jwt.CreateAccessToken(user, roles, permissions),
            RefreshToken = newToken,
            ExpiresIn = _jwt.AccessTokenTtlSeconds
        };
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        var tokenHash = TokenHasher.Sha256Hex(refreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (stored is null || stored.RevokedAt is not null)
            return;

        stored.RevokedAt = DateTime.UtcNow;
        stored.RevokeReason = "logout";
        await _db.SaveChangesAsync(ct);
    }

    public async Task<CurrentUserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("用户", userId);

        return new CurrentUserDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Roles = user.UserRoles.Select(ur => ur.Role.Code).Distinct().ToList(),
            Permissions = user.UserRoles.SelectMany(ur => ur.Role.RolePermissions)
                .Select(rp => rp.Permission.Code).Distinct().ToList()
        };
    }

    private async Task<string> CreateRefreshTokenAsync(User user, string? clientIp, string? userAgent, CancellationToken ct)
    {
        var ttlDays = await _settings.GetIntAsync("refresh_token_ttl_days", _jwt.RefreshTokenTtlDays, ct);
        var rawToken = TokenHasher.GenerateToken(48);

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = TokenHasher.Sha256Hex(rawToken),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(ttlDays),
            CreatedIp = clientIp,
            UserAgent = userAgent is { Length: > 512 } ? userAgent[..512] : userAgent
        });

        return rawToken;
    }

    private async Task RevokeAllUserTokensAsync(Guid userId, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.RevokeReason, reason), ct);
    }
}
