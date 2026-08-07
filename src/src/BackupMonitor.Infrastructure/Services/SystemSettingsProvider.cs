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

    public async Task<string?> GetStringAsync(string key, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (settings.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String)
            return element.GetString();
        return null;
    }

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
