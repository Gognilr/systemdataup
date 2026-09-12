using BackupMonitor.Core.Entities.System;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 每日健康快报的开关与发送时刻。
///
/// 这两个键一直在 system_settings 里、worker 也一直在读，但界面上没有入口——
/// 于是每次想开关它都只能连进数据库改一行 JSON，或者专门写一个迁移脚本。
/// 我们这两天就是这么干的，这本身就说明界面上缺了这一块。
/// </summary>
[Route("api/v1/admin/daily-digest")]
public class AdminDailyDigestController : ApiBaseController
{
    private readonly AppDbContext _db;
    private readonly SystemSettingsProvider _settings;

    public AdminDailyDigestController(AppDbContext db, SystemSettingsProvider settings)
    {
        _db = db;
        _settings = settings;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<DailyDigestSettingsDto>>> Get(CancellationToken ct)
    {
        return OkData(new DailyDigestSettingsDto
        {
            Enabled = await _settings.GetBoolAsync(DailyDigestWorker.EnabledKey, false, ct),
            Hour = Math.Clamp(
                await _settings.GetIntAsync(DailyDigestWorker.HourKey, DailyDigestWorker.DefaultHour, ct), 0, 23)
        });
    }

    [HttpPut]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<DailyDigestSettingsDto>>> Update(
        [FromBody] DailyDigestSettingsDto request, CancellationToken ct)
    {
        // 夹回合法区间而不是报错：填过头是手滑，不是攻击。
        var result = new DailyDigestSettingsDto
        {
            Enabled = request.Enabled,
            Hour = Math.Clamp(request.Hour, 0, 23)
        };

        await UpsertAsync(DailyDigestWorker.EnabledKey, result.Enabled ? "true" : "false", ct);
        await UpsertAsync(DailyDigestWorker.HourKey, result.Hour.ToString(), ct);
        await _db.SaveChangesAsync(ct);

        // 缓存 TTL 是 60 秒。不清的话，刚点了保存正盯着页面的人在那一分钟里看到的仍是旧值，
        // 会以为没保存上而再点一次。
        _settings.Invalidate();

        return OkData(result, "健康快报设置已保存");
    }

    private async Task UpsertAsync(string key, string jsonValue, CancellationToken ct)
    {
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == key, ct);
        if (row is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                SettingKey = key,
                SettingValue = jsonValue,
                Encrypted = false,
                UpdatedBy = UserId
            });
            return;
        }

        row.SettingValue = jsonValue;
        row.UpdatedBy = UserId;
    }
}
