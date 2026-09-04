using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 管理端传输进度。
///
/// 与 api/v1/agent/upload-sessions 是两回事：那一组是 Agent 自己续传要用的，
/// 走客户端证书鉴权，一个会话只看得到自己。这一组是给人看的，看全部在传的会话。
/// </summary>
[Route("api/v1/admin/upload-sessions")]
public class AdminUploadProgressController : ApiBaseController
{
    private readonly IUploadProgressService _progress;
    private readonly IUploadSessionControlService _control;

    public AdminUploadProgressController(IUploadProgressService progress, IUploadSessionControlService control)
    {
        _progress = progress;
        _control = control;
    }

    /// <summary>当前在传的会话及其进度、速度、预计剩余时间</summary>
    [HttpGet("active")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<UploadProgressDto>>>> Active(CancellationToken ct)
    {
        var result = await _progress.GetActiveAsync(ct);
        return OkData(result);
    }

    /// <summary>
    /// 最近结束的传输。默认不在界面上展开——常态下人要看的是在传的那几条；
    /// 但「昨晚那条为什么没传上去」只有这里答得上来。
    /// </summary>
    [HttpGet("finished")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<FinishedTransferDto>>>> Finished(
        [FromQuery] PagedQuery query, CancellationToken ct)
    {
        var result = await _progress.GetRecentFinishedAsync(query, ct);
        return OkData(result);
    }

    /// <summary>全局上传闸的状态：几个在传、几个在排队、上限是多少</summary>
    [HttpGet("queue-status")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<UploadQueueStatusDto>>> QueueStatus(CancellationToken ct)
    {
        var result = await _progress.GetQueueStatusAsync(ct);
        return OkData(result);
    }

    /// <summary>
    /// 暂停这次传输（待办方案 E）。会话置 paused 之后不再接受分块写入，
    /// 暂存与已传的块都留着，恢复时从断点继续。
    /// </summary>
    [HttpPost("{sessionId:guid}/pause")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.upload")]
    public async Task<ActionResult<ApiResponse>> Pause(Guid sessionId, CancellationToken ct)
    {
        await _control.PauseAsync(sessionId, ct);
        return OkMessage("已暂停，恢复时会从断点继续");
    }

    /// <summary>恢复：放回可写状态并重新下发上传指令</summary>
    [HttpPost("{sessionId:guid}/resume")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.upload")]
    public async Task<ActionResult<ApiResponse>> Resume(Guid sessionId, CancellationToken ct)
    {
        await _control.ResumeAsync(sessionId, ct);
        return OkMessage("已恢复，客户端会从断点继续传");
    }

    /// <summary>取消这次传输（已入库的不能取消）</summary>
    [HttpPost("{sessionId:guid}/cancel")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.upload")]
    public async Task<ActionResult<ApiResponse>> Cancel(Guid sessionId, CancellationToken ct)
    {
        await _control.CancelAsync(sessionId, ct);
        return OkMessage("这次传输已取消");
    }
}
