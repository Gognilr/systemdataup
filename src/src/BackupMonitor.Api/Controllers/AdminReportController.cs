using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端报表统计（第二批补充设计：只读聚合，按域复用 read 权限）</summary>
[Route("api/v1/admin/reports")]
public class AdminReportController : ApiBaseController
{
    private readonly IReportService _reportService;

    public AdminReportController(IReportService reportService)
    {
        _reportService = reportService;
    }

    /// <summary>客户端状态汇总</summary>
    [HttpGet("client-summary")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<ClientSummaryReportDto>>> ClientSummary(CancellationToken ct)
    {
        var result = await _reportService.GetClientSummaryAsync(ct);
        return OkData(result);
    }

    /// <summary>备份统计汇总（含最近 14 天按日上传统计）</summary>
    [HttpGet("backup-summary")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.read")]
    public async Task<ActionResult<ApiResponse<BackupSummaryReportDto>>> BackupSummary(CancellationToken ct)
    {
        var result = await _reportService.GetBackupSummaryAsync(ct);
        return OkData(result);
    }

    /// <summary>告警汇总</summary>
    [HttpGet("alert-summary")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.read")]
    public async Task<ActionResult<ApiResponse<AlertSummaryReportDto>>> AlertSummary(CancellationToken ct)
    {
        var result = await _reportService.GetAlertSummaryAsync(ct);
        return OkData(result);
    }

    /// <summary>任务执行汇总</summary>
    [HttpGet("task-summary")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<TaskSummaryReportDto>>> TaskSummary(CancellationToken ct)
    {
        var result = await _reportService.GetTaskSummaryAsync(ct);
        return OkData(result);
    }
}
