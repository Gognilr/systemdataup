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
/// 告警触发阈值（整改清单 2026-09-11 · R25）。
///
/// 这四个键一直都在 system_settings 里、代码也一直在读，但界面上没有任何入口：
/// 现场要把 CPU 阈值从 85% 调到 95%（一台常年满负荷的 ERP 服务器），
/// 唯一的办法是连进数据库改一行 JSON，而知道该改哪个键的人只有写代码的。
/// 于是实际发生的是另一件事——没人调，告警每天照报，报到没人看。
///
/// 与通知筛选（<c>notification-settings</c> 里的 filter）分工明确：
/// **这里决定告警什么时候产生，那里决定产生之后发不发。**
/// 两者不能互相替代：阈值调高会让告警在网页上也消失，筛选只是不打扰人。
/// </summary>
[Route("api/v1/admin/alert-thresholds")]
public class AdminAlertThresholdController : ApiBaseController
{
    /// <summary>与 AgentHeartbeatService.EvaluateResourceAlertsAsync 读的是同一批键。</summary>
    public const string CpuKey = "client_cpu_alert_percent";

    public const string MemoryKey = "client_memory_alert_percent";

    public const string DiskFreeKey = "client_disk_free_alert_percent";

    private readonly AppDbContext _db;
    private readonly SystemSettingsProvider _settings;

    public AdminAlertThresholdController(AppDbContext db, SystemSettingsProvider settings)
    {
        _db = db;
        _settings = settings;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<AlertThresholdSettingsDto>>> Get(CancellationToken ct)
    {
        return OkData(new AlertThresholdSettingsDto
        {
            ClientCpuPercent = Math.Clamp(await _settings.GetIntAsync(CpuKey, 85, ct), 1, 100),
            ClientMemoryPercent = Math.Clamp(await _settings.GetIntAsync(MemoryKey, 90, ct), 1, 100),
            ClientDiskFreePercent = Math.Clamp(await _settings.GetIntAsync(DiskFreeKey, 10, ct), 1, 99),
            RenotifyHours = Math.Clamp(
                await _settings.GetIntAsync(
                    AlertingService.RenotifyHoursKey, AlertingService.DefaultRenotifyHours, ct), 1, 720),
            RecoveryNotify = await _settings.GetBoolAsync(AlertingService.RecoveryNotifyKey, true, ct)
        });
    }

    [HttpPut]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<AlertThresholdSettingsDto>>> Update(
        [FromBody] AlertThresholdSettingsDto request, CancellationToken ct)
    {
        // 夹回合法区间而不是报错：四个都是「松一点 / 紧一点」的旋钮，填过头是手滑。
        var result = new AlertThresholdSettingsDto
        {
            ClientCpuPercent = Math.Clamp(request.ClientCpuPercent, 1, 100),
            ClientMemoryPercent = Math.Clamp(request.ClientMemoryPercent, 1, 100),
            ClientDiskFreePercent = Math.Clamp(request.ClientDiskFreePercent, 1, 99),
            RenotifyHours = Math.Clamp(request.RenotifyHours, 1, 720),
            RecoveryNotify = request.RecoveryNotify
        };

        await UpsertAsync(CpuKey, result.ClientCpuPercent.ToString(), ct);
        await UpsertAsync(MemoryKey, result.ClientMemoryPercent.ToString(), ct);
        await UpsertAsync(DiskFreeKey, result.ClientDiskFreePercent.ToString(), ct);
        await UpsertAsync(AlertingService.RenotifyHoursKey, result.RenotifyHours.ToString(), ct);
        await UpsertAsync(
            AlertingService.RecoveryNotifyKey, result.RecoveryNotify ? "true" : "false", ct);
        await _db.SaveChangesAsync(ct);

        // 缓存 TTL 是 60 秒。不清缓存的话，刚点了保存正盯着页面的人
        // 在那一分钟里看到的仍是旧值，会以为没保存上而再点一次。
        _settings.Invalidate();

        return OkData(result,
            "告警阈值已保存。已经产生的告警不受影响，下一次心跳按新阈值判定。");
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
