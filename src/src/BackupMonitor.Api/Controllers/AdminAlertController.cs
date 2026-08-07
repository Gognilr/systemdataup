using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端告警（设计书 20）</summary>
[Route("api/v1/admin/alerts")]
public class AdminAlertController : ApiBaseController
{
    private readonly IAlertService _alertService;

    public AdminAlertController(IAlertService alertService)
    {
        _alertService = alertService;
    }

    /// <summary>告警列表（20.1，默认仅活动告警）</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<AlertListItemDto>>>> List(
        [FromQuery] AlertQuery query, CancellationToken ct)
    {
        var result = await _alertService.GetListAsync(query, ct);
        return OkData(result);
    }

    /// <summary>告警详情</summary>
    [HttpGet("{alertId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.read")]
    public async Task<ActionResult<ApiResponse<AlertDetailDto>>> Detail(Guid alertId, CancellationToken ct)
    {
        var result = await _alertService.GetDetailAsync(alertId, ct);
        return OkData(result);
    }

    /// <summary>确认告警（20.2）</summary>
    [HttpPost("{alertId:guid}/acknowledge")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.handle")]
    public async Task<ActionResult<ApiResponse>> Acknowledge(Guid alertId, CancellationToken ct)
    {
        await _alertService.AcknowledgeAsync(alertId, ct);
        return OkMessage("告警已确认");
    }

    /// <summary>更新处理状态（20.3：in_progress / ignored）</summary>
    [HttpPost("{alertId:guid}/handle")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.handle")]
    public async Task<ActionResult<ApiResponse>> Handle(
        Guid alertId, [FromBody] HandleAlertRequest request, CancellationToken ct)
    {
        await _alertService.HandleAsync(alertId, request, ct);
        return OkMessage("告警状态已更新");
    }

    /// <summary>关闭告警（20.4）</summary>
    [HttpPost("{alertId:guid}/close")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.handle")]
    public async Task<ActionResult<ApiResponse>> Close(
        Guid alertId, [FromBody] CloseAlertRequest request, CancellationToken ct)
    {
        await _alertService.CloseAsync(alertId, request, ct);
        return OkMessage("告警已关闭");
    }
}
