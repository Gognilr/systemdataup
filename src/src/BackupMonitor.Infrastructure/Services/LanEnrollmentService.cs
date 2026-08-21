using System.Text.Json;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>LAN 免令牌登记窗口；安装默认窗口来自配置，管理员临时开放窗口写入数据库。</summary>
public sealed class LanEnrollmentService
{
    public const string OpenUntilSettingKey = "lan_enrollment_open_until";
    private readonly AppDbContext _db;

    public LanEnrollmentService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DateTime?> GetOpenUntilAsync(DateTime? configuredUntilUtc, CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SettingKey == OpenUntilSettingKey, ct);
        if (row is null)
            return configuredUntilUtc?.ToUniversalTime();

        try
        {
            using var document = JsonDocument.Parse(row.SettingValue);
            if (document.RootElement.ValueKind == JsonValueKind.String
                && document.RootElement.TryGetDateTime(out var value))
                return value.ToUniversalTime();
        }
        catch (JsonException)
        {
            // 配置损坏时回退到启动配置，不放宽登记窗口。
        }

        return configuredUntilUtc?.ToUniversalTime();
    }

    public async Task<DateTime> OpenForAsync(Guid? updatedBy, TimeSpan duration, CancellationToken ct = default)
    {
        var expiresAt = DateTime.UtcNow.Add(duration);
        var row = await _db.SystemSettings.FirstOrDefaultAsync(
            s => s.SettingKey == OpenUntilSettingKey,
            ct);
        if (row is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                SettingKey = OpenUntilSettingKey,
                SettingValue = JsonSerializer.Serialize(expiresAt),
                Encrypted = false,
                UpdatedBy = updatedBy,
                UpdatedAt = DateTime.UtcNow,
                RowVersion = 1
            });
        }
        else
        {
            row.SettingValue = JsonSerializer.Serialize(expiresAt);
            row.Encrypted = false;
            row.UpdatedBy = updatedBy;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return expiresAt;
    }
}
