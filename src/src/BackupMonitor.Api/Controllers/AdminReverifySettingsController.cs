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
/// 定期复查设置（整改清单 2026-09-10 · R18）。
///
/// 这一层抓的是**入库之后**才会发生的三件事：磁盘静默损坏、误删、勒索软件加密。
/// 这三样入库那一次核对无论多严格都看不到——它们全都发生在核对完成之后。
///
/// 在此之前它没有关闭的办法，也没有任何界面：间隔 clamp 在 1~720 小时，
/// 现场真要临时停掉（比如正在做全量迁移、盘已经满负荷）只能把间隔改成 720 小时，
/// 那是「关掉」的一个变相写法，而且没人看得出它被关过。
/// </summary>
[Route("api/v1/admin/reverify-settings")]
public class AdminReverifySettingsController : ApiBaseController
{
    private readonly AppDbContext _db;
    private readonly SystemSettingsProvider _settings;

    public AdminReverifySettingsController(AppDbContext db, SystemSettingsProvider settings)
    {
        _db = db;
        _settings = settings;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<ReverifySettingsDto>>> Get(CancellationToken ct)
    {
        return OkData(new ReverifySettingsDto
        {
            Enabled = await _settings.GetBoolAsync(BackupReverifyWorker.EnabledKey, true, ct),
            IntervalHours = Math.Clamp(
                await _settings.GetIntAsync(
                    BackupReverifyWorker.IntervalHoursKey, BackupReverifyWorker.DefaultIntervalHours, ct), 1, 720),
            BatchSize = Math.Clamp(
                await _settings.GetIntAsync(
                    BackupReverifyWorker.BatchSizeKey, BackupReverifyWorker.DefaultBatchSize, ct), 1, 200)
        });
    }

    [HttpPut]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<ReverifySettingsDto>>> Update(
        [FromBody] ReverifySettingsDto request, CancellationToken ct)
    {
        // 夹回合法区间而不是报错：这三个值都是「调稀 / 调密」的旋钮，
        // 填过头是手滑，不是需要拦下来的错误。
        var interval = Math.Clamp(request.IntervalHours, 1, 720);
        var batch = Math.Clamp(request.BatchSize, 1, 200);

        await UpsertAsync(BackupReverifyWorker.EnabledKey, request.Enabled ? "true" : "false", ct);
        await UpsertAsync(BackupReverifyWorker.IntervalHoursKey, interval.ToString(), ct);
        await UpsertAsync(BackupReverifyWorker.BatchSizeKey, batch.ToString(), ct);
        await _db.SaveChangesAsync(ct);

        // 缓存 TTL 是 60 秒。不清缓存的话，刚点了保存正盯着页面的人
        // 在那一分钟里看到的仍是旧值，会以为没保存上而再点一次。
        _settings.Invalidate();

        return OkData(
            new ReverifySettingsDto { Enabled = request.Enabled, IntervalHours = interval, BatchSize = batch },
            request.Enabled ? "定期复查设置已保存" : "定期复查已停用，不会再产生复查工作项");
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
