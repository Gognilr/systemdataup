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

    /// <summary>修改当前用户口令（校验旧口令 + 强度校验 + 吊销全部刷新令牌，OPEN-ISSUES #2）</summary>
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);
}

/// <summary>管理员认证实现（bcrypt 密码校验 + JWT + 可吊销刷新令牌轮换）</summary>
public class AuthService : IAuthService
{
    // OPEN-ISSUES #7：用户不存在时也执行一次 bcrypt 校验（成本 12 与真实哈希一致），
    // 使攻击者无法通过响应时延探测用户名是否存在。
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy", workFactor: 12);

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
            // OPEN-ISSUES #7：先做一次等成本的 bcrypt 校验，避免时延泄露用户名存在性
            BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
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
            MustChangePassword = user.MustChangePassword,
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

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("用户", userId);

        // 1. 校验旧口令：错误时仅记审计，不做任何状态变更
        bool oldOk;
        try
        {
            oldOk = BCrypt.Net.BCrypt.Verify(request.OldPassword, user.PasswordHash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "旧口令哈希校验异常 user={Username}", user.Username);
            oldOk = false;
        }

        if (!oldOk)
        {
            await _audit.RecordAsync("auth.change_password", AuditResult.Failure, "user", user.Id,
                errorMessage: "旧口令错误", ct: ct);
            throw new BusinessException("UNAUTHORIZED", "旧口令错误", 401);
        }

        // 2. 强度校验放在服务端（OPEN-ISSUES #2），不能只依赖前端
        var strengthError = ValidatePasswordStrength(request.NewPassword, user.Username);
        if (strengthError is not null)
            throw new BusinessException("INVALID_REQUEST", strengthError, 400);

        // 3. 更新口令（bcrypt 成本 12，与 V001/V005 的 gen_salt('bf',12) 一致）
        var now = DateTime.UtcNow;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, workFactor: 12);
        user.PasswordChangedAt = now;
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);

        // 4. 吊销该用户全部刷新令牌，强制所有会话用新口令重新登录
        //    （访问令牌为无状态 JWT，仍有效至自然过期——符合验收口径）
        await RevokeAllUserTokensAsync(user.Id, "password_changed", ct);

        await _audit.RecordAsync("auth.change_password", AuditResult.Success, "user", user.Id, ct: ct);
        _logger.LogInformation("用户 {Username} 口令修改成功，已吊销其全部刷新令牌", user.Username);
    }

    /// <summary>口令强度校验：长度 ≥ 12、不等于用户名、不属于常见弱口令。返回错误信息，null 表示通过</summary>
    private static string? ValidatePasswordStrength(string newPassword, string username)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 12)
            return "新口令长度不得少于 12 位";

        if (string.Equals(newPassword, username, StringComparison.OrdinalIgnoreCase))
            return "新口令不能与用户名相同";

        if (CommonWeakPasswords.Contains(newPassword.ToLowerInvariant()))
            return "新口令属于常见弱口令，请更换";

        return null;
    }

    /// <summary>常见弱口令表（长度 ≥ 12 的条目仍可能被弱口令字典收录，兜底拦截）</summary>
    private static readonly HashSet<string> CommonWeakPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password123456", "123456789012", "abcdefghijkl", "qwertyuiop12",
        "admin@2026!!!!", "administrator1", "changeme1234", "welcome12345",
        "aa123456789012", "111111111111", "000000000000"
    };

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
