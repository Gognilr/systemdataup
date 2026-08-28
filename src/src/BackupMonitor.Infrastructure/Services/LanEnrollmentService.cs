using System.Text.Json;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>LAN 免令牌登记窗口；安装默认窗口来自配置，管理员临时开放窗口写入数据库。</summary>
public sealed class LanEnrollmentService
{
    public const string OpenUntilSettingKey = "lan_enrollment_open_until";

    /// <summary>
    /// 点一次「再开放」延长多久。界面不再自己写死一个数：
    /// 按钮上写「开放 30 分钟」而实际显示的窗口是安装默认的 24 小时，
    /// 两个数字对不上，管理员自然会怀疑这个功能没生效。
    /// </summary>
    public static readonly TimeSpan ExtendDuration = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _db;

    public LanEnrollmentService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DateTime?> GetOpenUntilAsync(DateTime? configuredUntilUtc, CancellationToken ct = default)
        => (await GetWindowAsync(configuredUntilUtc, ct)).OpenUntilUtc;

    /// <summary>
    /// 当前窗口，以及它是「有人手动开的」还是「安装时的默认值」。
    /// 界面要能说清这个到期时间是哪来的——否则一个没人动过的 24 小时窗口
    /// 看起来就像功能失效。
    /// </summary>
    public async Task<LanEnrollmentWindow> GetWindowAsync(DateTime? configuredUntilUtc, CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SettingKey == OpenUntilSettingKey, ct);
        if (row is null)
            return new LanEnrollmentWindow(configuredUntilUtc?.ToUniversalTime(), ManuallyOpened: false);

        try
        {
            using var document = JsonDocument.Parse(row.SettingValue);
            if (document.RootElement.ValueKind == JsonValueKind.String
                && document.RootElement.TryGetDateTime(out var value))
                return new LanEnrollmentWindow(value.ToUniversalTime(), ManuallyOpened: true);
        }
        catch (JsonException)
        {
            // 配置损坏时回退到启动配置，不放宽登记窗口。
        }

        return new LanEnrollmentWindow(configuredUntilUtc?.ToUniversalTime(), ManuallyOpened: false);
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

/// <summary>
/// 免令牌登记窗口的当前状态。
/// ManuallyOpened=false 表示这个到期时间来自安装默认值（服务端首次启动 + 24 小时），
/// 没有任何人开过窗口。
/// </summary>
public readonly record struct LanEnrollmentWindow(DateTime? OpenUntilUtc, bool ManuallyOpened);
