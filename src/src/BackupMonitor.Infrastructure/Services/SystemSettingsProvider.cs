using System.Text.Json;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 系统配置提供器（system_settings 表，60 秒内存缓存）。
/// setting_value 为 jsonb 标量（如 60、"E:\BackupRepository"）。
/// </summary>
public class SystemSettingsProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemSettingsProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, JsonElement> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _cacheExpiresAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public SystemSettingsProvider(IServiceScopeFactory scopeFactory, ILogger<SystemSettingsProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (settings.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var value))
            return value;
        return defaultValue;
    }

    public async Task<long> GetLongAsync(string key, long defaultValue, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (settings.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out var value))
            return value;
        return defaultValue;
    }

    /// <summary>
    /// 开关类配置。jsonb 里既可能是 true/false，也可能被人手工写成 "true" 字符串——
    /// 两种都认，认不出来才退回默认值。开关静默失效的后果是「以为关了其实还在跑」，
    /// 而这类误会在排障时能耗掉一整天。
    /// </summary>
    public async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.TryGetValue(key, out var element))
            return defaultValue;

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    /// <summary>
    /// 比例类配置（如历史基线的偏离比例）用得着小数，而这些键在 jsonb 里就是 0.5 这样的数字。
    /// 走 GetIntAsync 会被 TryGetInt32 挡掉而静默退回默认值——那种失效不会有任何报错。
    /// </summary>
    public async Task<double> GetDoubleAsync(string key, double defaultValue, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (settings.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out var value))
            return value;
        return defaultValue;
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (settings.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String)
            return element.GetString();
        return null;
    }

    /// <summary>
    /// 丢弃缓存，下一次读取直接回库。
    /// 管理端改完配置必须调一次：60 秒 TTL 对后台工作器无所谓，但对刚点了保存、
    /// 正盯着页面看新路径生不生效的人来说，那一分钟里界面显示的是旧值。
    /// </summary>
    public void Invalidate() => _cacheExpiresAt = DateTime.MinValue;

    private async Task<Dictionary<string, JsonElement>> GetSettingsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _cacheExpiresAt)
            return _cache;

        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _cacheExpiresAt)
                return _cache;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var rows = await db.SystemSettings.AsNoTracking().ToListAsync(ct);
            var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                try
                {
                    map[row.SettingKey] = JsonDocument.Parse(row.SettingValue).RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "系统配置 {Key} 的 jsonb 值解析失败", row.SettingKey);
                }
            }

            _cache = map;
            _cacheExpiresAt = DateTime.UtcNow.Add(CacheTtl);
            return _cache;
        }
        catch (Exception ex)
        {
            // 数据库不可用时返回旧缓存（或空），避免阻塞全部接口
            _logger.LogError(ex, "加载系统配置失败，使用缓存/默认值");
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }
}
