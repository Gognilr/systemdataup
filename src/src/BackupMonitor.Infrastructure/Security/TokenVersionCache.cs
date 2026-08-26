using System.Collections.Concurrent;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Security;

/// <summary>
/// 令牌版本号缓存（整改批次 C · C4）。
///
/// 权限烤死在 access token 里，撤权/禁用账号/改角色/改密/登出之后，已签发的令牌本来要等到
/// 自然过期（默认 3600 秒）才失效。这里比对 token 里的 tv claim 与库中 users.token_version，
/// 不一致即视为令牌已作废。为避免每个请求都查一次库，按 userId 缓存 60 秒——
/// 形状与 SystemSettingsProvider 一致：TTL 到期前直接返回内存值，缓存命中零 DB 查询；
/// 最坏情况下撤权 60 秒后才生效，比原来的最长 1 小时好两个数量级。
/// </summary>
public class TokenVersionCache
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>查询失败（数据库抖动）且无旧缓存可用时的返回值：约定为"跳过本次校验"，
    /// 避免数据库短暂不可用导致全站管理员被瞬间登出。</summary>
    public const int SkipCheck = -2;

    /// <summary>用户不存在（已被删除）时的返回值，必然与任何真实 tv 不相等，从而触发 401。</summary>
    public const int UserNotFound = -1;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TokenVersionCache> _logger;
    private readonly ConcurrentDictionary<Guid, (int Version, DateTime ExpiresAtUtc)> _cache = new();

    public TokenVersionCache(IServiceScopeFactory scopeFactory, ILogger<TokenVersionCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>取用户当前的 token_version（缓存命中不产生 DB 查询）</summary>
    public async Task<int> GetCurrentVersionAsync(Guid userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out var cached) && DateTime.UtcNow < cached.ExpiresAtUtc)
            return cached.Version;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var version = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => (int?)u.TokenVersion)
                .FirstOrDefaultAsync(ct);

            var resolved = version ?? UserNotFound;
            _cache[userId] = (resolved, DateTime.UtcNow.Add(CacheTtl));
            return resolved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询用户 {UserId} 的令牌版本号失败", userId);
            // 数据库短暂不可用：有旧缓存就沿用旧值（哪怕已过期），没有就放行本次校验
            return _cache.TryGetValue(userId, out var stale) ? stale.Version : SkipCheck;
        }
    }

    /// <summary>使某用户的缓存立即失效（可选：改密/禁用等操作后调用，不必等 60 秒 TTL 自然过期）</summary>
    public void Invalidate(Guid userId) => _cache.TryRemove(userId, out _);
}
