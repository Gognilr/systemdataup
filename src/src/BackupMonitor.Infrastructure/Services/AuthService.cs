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

/// <summary>
/// 管理员认证服务（设计书 9 认证接口 / 23.2 管理认证）。
///
/// 整改批次 C · C4：本文件在改密（ChangePasswordAsync）和登出（LogoutAsync）时把
/// user.TokenVersion++，签发的 JWT 带同名 tv claim，TokenVersionMiddleware 据此在
/// 60 秒内拒掉旧令牌，不必等它自然过期。
///
/// 产品形态是单管理员，不做用户管理（多账号、角色、逐项授权都不在范围内），
/// 所以没有"禁用账号 / 改角色 / 改权限"这类撤权动作，tv 的触发点就只有改密和登出这两个。
/// 若日后真要加多用户，那些接口必须同样 TokenVersion++（写法参考本文件），
/// 否则权限会烤死在已签发的令牌里直到自然过期。
/// </summary>
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

    /// <summary>
    /// 令牌版本缓存（审计 H-14）。它是单例，注入到 scoped 服务里没问题——
    /// 这里只调 Invalidate，不持有任何请求级状态。
    /// </summary>
    private readonly TokenVersionCache _tokenVersions;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        JwtTokenService jwt,
        SystemSettingsProvider settings,
        IAuditRecorder audit,
        TokenVersionCache tokenVersions,
        ILogger<AuthService> logger)
    {
        _db = db;
        _jwt = jwt;
        _settings = settings;
        _audit = audit;
        _tokenVersions = tokenVersions;
        _logger = logger;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, string? clientIp, string? userAgent, CancellationToken ct = default)
    {
        // OPEN-ISSUES #4：主查询不带 Include（两层集合导航在 SingleQuery 下是笛卡尔积）。
        // user 本身仍需跟踪——后面要写 FailedLoginCount / LockedUntil / LastLoginAt。
        var user = await _db.Users
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

        // 审查 P2-1：停用检查曾排在密码校验之前，且返回专有文案「账号已停用」，
        // 与上面「统一错误信息，不暴露用户是否存在」的意图自相矛盾——
        // 未认证攻击者据此即可枚举出哪些用户名真实存在。
        // 状态校验统一放到密码校验通过之后（见下方 user.Status != Active 分支）。

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

        // OPEN-ISSUES #4：角色码/权限码改用两条窄投影查询（无笛卡尔积、无实体物化）
        var (roles, permissions) = await LoadRoleAndPermissionCodesAsync(user.Id, ct);

        var accessToken = _jwt.CreateAccessToken(user, roles, permissions);
        var refreshTokenTtlDays = await _settings.GetIntAsync("refresh_token_ttl_days", _jwt.RefreshTokenTtlDays, ct);
        var refreshToken = await CreateRefreshTokenAsync(user, clientIp, userAgent, ct);

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("auth.login", AuditResult.Success, "user", user.Id, ct: ct);

        return new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresIn = _jwt.AccessTokenTtlSeconds,
            RefreshTokenTtlDays = refreshTokenTtlDays,
            MustChangePassword = user.MustChangePassword,
            User = new CurrentUserDto
            {
                Id = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Roles = roles,
                Permissions = permissions,
                MustChangePassword = user.MustChangePassword
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
            // 被吊销的令牌再次使用：这是典型的令牌泄露信号。
            //
            // 审计 H-14：原先只吊销刷新令牌，没有 TokenVersion++。
            // 刷新令牌是有状态的，吊销立刻生效；访问令牌是无状态 JWT，
            // 不动 TokenVersion 就得等它自然过期——最长一小时。
            // 也就是说在确认令牌泄露的那一刻，攻击者手上的访问令牌仍然全权有效，
            // 而这一小时正是最需要立刻切断的时候。改密和登出都做了这一步，
            // 唯独安全性最敏感的重放检测漏了。
            _logger.LogWarning("检测到已吊销刷新令牌重用 user={UserId}，吊销该用户全部刷新令牌并作废已签发的访问令牌", stored.UserId);
            await RevokeAllUserTokensAsync(stored.UserId, "token_reuse", ct);

            var compromised = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
            if (compromised is not null)
            {
                compromised.TokenVersion++;
                await _db.SaveChangesAsync(ct);
                _tokenVersions.Invalidate(compromised.Id);
            }

            // 令牌重放是最该留痕的安全事件之一，此前一条审计都没有。
            await _audit.RecordAsync("auth.token_reuse", AuditResult.Failure, "user", stored.UserId, ct: ct);

            throw new BusinessException("UNAUTHORIZED", "刷新令牌已失效，请重新登录", 401);
        }

        if (stored.ExpiresAt <= now)
            throw new BusinessException("UNAUTHORIZED", "刷新令牌已过期，请重新登录", 401);

        if (stored.User.Status != UserStatus.Active)
            throw new BusinessException("FORBIDDEN", "账号状态异常，无法刷新令牌", 403);

        // OPEN-ISSUES #4：去掉两层集合 Include，角色/权限走投影查询
        var user = await _db.Users.FirstAsync(u => u.Id == stored.UserId, ct);

        // 轮换：旧令牌标记吊销，签发新令牌
        var refreshTokenTtlDays = await _settings.GetIntAsync("refresh_token_ttl_days", _jwt.RefreshTokenTtlDays, ct);
        var newToken = await CreateRefreshTokenAsync(user, clientIp, userAgent, ct);
        stored.RevokedAt = now;
        stored.RevokeReason = "rotated";
        stored.ReplacedByHash = TokenHasher.Sha256Hex(newToken);

        var (roles, permissions) = await LoadRoleAndPermissionCodesAsync(user.Id, ct);

        await _db.SaveChangesAsync(ct);

        return new RefreshTokenResponse
        {
            AccessToken = _jwt.CreateAccessToken(user, roles, permissions),
            RefreshToken = newToken,
            ExpiresIn = _jwt.AccessTokenTtlSeconds,
            RefreshTokenTtlDays = refreshTokenTtlDays
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

        // 整改批次 C · C4：登出时令牌版本号 ++，让刷新令牌与访问令牌一起失效——
        // 访问令牌本身无状态（JWT），此前只能等它自然过期（最长 1 小时）；
        // 现在 TokenVersionMiddleware 最坏 60 秒内就会因为 tv 不匹配拒绝这次登出会话下发的令牌。
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is not null)
            user.TokenVersion++;

        await _db.SaveChangesAsync(ct);

        // 审计 H-14：TokenVersionCache.Invalidate 写好了却一处都没被调用，
        // 于是登出实际要等满 60 秒 TTL 才生效。撤权要即时。
        if (user is not null)
            _tokenVersions.Invalidate(user.Id);
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

        // 2. 新口令不得与当前口令相同。
        //    这一条不能省：首次登录的强制改密（MustChangePassword）把「原样填回初始口令」
        //    当作一次成功改密，会清掉强制改密标记而口令仍是安装器给的那个默认值——
        //    强制改密这道关就等于自动放行了。此处 oldOk 已经证明 OldPassword 就是当前口令，
        //    直接比对明文即可，不必再做一次 bcrypt。
        if (string.Equals(request.OldPassword, request.NewPassword, StringComparison.Ordinal))
        {
            await _audit.RecordAsync("auth.change_password", AuditResult.Failure, "user", user.Id,
                errorMessage: "新口令与当前口令相同", ct: ct);
            throw new BusinessException("INVALID_REQUEST", "新口令不能与当前口令相同", 400);
        }

        // 3. 强度校验放在服务端（OPEN-ISSUES #2），不能只依赖前端
        var strengthError = PasswordPolicy.Validate(request.NewPassword, user.Username);
        if (strengthError is not null)
            throw new BusinessException("INVALID_REQUEST", strengthError, 400);

        // 4. 更新口令（bcrypt 成本 12，与 V001/V005 的 gen_salt('bf',12) 一致）
        var now = DateTime.UtcNow;
        user.PasswordHash = PasswordPolicy.Hash(request.NewPassword);
        user.PasswordChangedAt = now;
        user.MustChangePassword = false;

        // 整改批次 C · C4：令牌版本号 ++。此前这里的注释写"访问令牌为无状态 JWT，
        // 仍有效至自然过期"，就是权限/身份变更后旧令牌能继续用最长 1 小时的那个洞——
        // 现在 TokenVersionMiddleware 会在 60 秒缓存 TTL 内把已改密账号的旧访问令牌拒掉。
        user.TokenVersion++;

        await _db.SaveChangesAsync(ct);

        // 审计 H-14：同上，不清缓存的话新的 TokenVersion 最坏 60 秒后才被看见。
        _tokenVersions.Invalidate(user.Id);

        // 5. 吊销该用户全部刷新令牌，强制所有会话用新口令重新登录
        await RevokeAllUserTokensAsync(user.Id, "password_changed", ct);

        await _audit.RecordAsync("auth.change_password", AuditResult.Success, "user", user.Id, ct: ct);
        _logger.LogInformation("用户 {Username} 口令修改成功，已吊销其全部刷新令牌", user.Username);
    }

    public async Task<CurrentUserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct = default)
    {
        // OPEN-ISSUES #4：只读路径不再拉整个角色/权限对象图，改投影查询
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("用户", userId);

        var (roles, permissions) = await LoadRoleAndPermissionCodesAsync(userId, ct);

        return new CurrentUserDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Roles = roles,
            Permissions = permissions,
            MustChangePassword = user.MustChangePassword
        };
    }

    /// <summary>
    /// OPEN-ISSUES #4：取某用户的角色码与权限码。
    /// 原先用两层集合 Include（UserRoles→Role→RolePermissions→Permission）在 SingleQuery 下
    /// 产生笛卡尔积（3 角色 × 每角色 16 权限 = 48 行重复数据）；改为两条窄投影查询，
    /// 无重复行、无实体物化、AsNoTracking。
    /// </summary>
    private async Task<(List<string> Roles, List<string> Permissions)> LoadRoleAndPermissionCodesAsync(
        Guid userId, CancellationToken ct)
    {
        var roles = await _db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Code)
            .Distinct()
            .ToListAsync(ct);

        var permissions = await _db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .Distinct()
            .ToListAsync(ct);

        return (roles, permissions);
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
