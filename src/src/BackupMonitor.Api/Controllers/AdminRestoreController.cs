using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端恢复下载（设计书 19；令牌签发与列表为补充设计）</summary>
[Route("api/v1/admin/restore-requests")]
public class AdminRestoreController : ApiBaseController
{
    private readonly IRestoreService _restoreService;

    public AdminRestoreController(IRestoreService restoreService)
    {
        _restoreService = restoreService;
    }

    /// <summary>创建恢复请求（19.1，Idempotency-Key 头保证幂等）</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.download")]
    public async Task<ActionResult<ApiResponse<CreateRestoreResponseDto>>> Create(
        [FromBody] CreateRestoreRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var result = await _restoreService.CreateAsync(request, idempotencyKey, ct);
        return OkData(result, "恢复请求已创建，正在执行下载前完整性校验");
    }

    /// <summary>恢复请求列表（补充设计）</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.download")]
    public async Task<ActionResult<ApiResponse<PagedResult<RestoreRequestDto>>>> List(
        [FromQuery] RestoreQuery query, CancellationToken ct)
    {
        var result = await _restoreService.GetListAsync(query, ct);
        return OkData(result);
    }

    /// <summary>查询恢复请求（19.2）</summary>
    [HttpGet("{requestId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.download")]
    public async Task<ActionResult<ApiResponse<RestoreRequestDto>>> Get(Guid requestId, CancellationToken ct)
    {
        var result = await _restoreService.GetAsync(requestId, ct);
        return OkData(result);
    }

    /// <summary>签发/换发下载令牌（补充设计：明文令牌只在本响应中出现一次，换发即失效旧令牌）</summary>
    [HttpPost("{requestId:guid}/download-token")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.download")]
    public async Task<ActionResult<ApiResponse<RestoreDownloadTokenDto>>> IssueDownloadToken(Guid requestId, CancellationToken ct)
    {
        var result = await _restoreService.IssueDownloadTokenAsync(requestId, ct);
        return OkData(result, "下载令牌已签发，请在有效期内完成下载");
    }
}
