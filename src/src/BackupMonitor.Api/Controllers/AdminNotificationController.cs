using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端通知：发送记录查询 + 渠道配置（第二批补充设计；实际发送通道后续实现）</summary>
[Route("api/v1/admin")]
public class AdminNotificationController : ApiBaseController
{
    private readonly INotificationService _notificationService;

    public AdminNotificationController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    /// <summary>通知发送记录列表</summary>
    [HttpGet("notification-deliveries")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<NotificationDeliveryDto>>>> ListDeliveries(
        [FromQuery] NotificationDeliveryQuery query, CancellationToken ct)
    {
        var result = await _notificationService.GetDeliveriesAsync(query, ct);
        return OkData(result);
    }

    /// <summary>查询通知渠道配置</summary>
    [HttpGet("notification-settings")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<NotificationSettingsDto>>> GetSettings(CancellationToken ct)
    {
        var result = await _notificationService.GetSettingsAsync(ct);
        return OkData(result);
    }

    /// <summary>更新通知渠道配置（存 system_settings）</summary>
    [HttpPut("notification-settings")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<NotificationSettingsDto>>> UpdateSettings(
        [FromBody] NotificationSettingsDto request, CancellationToken ct)
    {
        var result = await _notificationService.UpdateSettingsAsync(request, ct);
        return OkData(result, "通知渠道配置已保存");
    }
}
