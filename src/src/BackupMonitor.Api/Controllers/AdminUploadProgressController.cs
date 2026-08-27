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

    public AdminUploadProgressController(IUploadProgressService progress)
    {
        _progress = progress;
    }

    /// <summary>当前在传的会话及其进度、速度、预计剩余时间</summary>
    [HttpGet("active")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<UploadProgressDto>>>> Active(CancellationToken ct)
    {
        var result = await _progress.GetActiveAsync(ct);
        return OkData(result);
    }
}
