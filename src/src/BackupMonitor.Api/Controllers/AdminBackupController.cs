using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端备份查询与管理（设计书 18）</summary>
[Route("api/v1/admin/backups")]
public class AdminBackupController : ApiBaseController
{
    private readonly IBackupSetService _backupService;

    public AdminBackupController(IBackupSetService backupService)
    {
        _backupService = backupService;
    }

    /// <summary>备份列表（18.1）</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<BackupSetListItemDto>>>> List(
        [FromQuery] BackupQuery query, CancellationToken ct)
    {
        var result = await _backupService.GetListAsync(query, ct);
        return OkData(result);
    }

    /// <summary>备份详情（18.2）</summary>
    [HttpGet("{backupSetId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.read")]
    public async Task<ActionResult<ApiResponse<BackupSetDetailDto>>> Detail(Guid backupSetId, CancellationToken ct)
    {
        var result = await _backupService.GetDetailAsync(backupSetId, ct);
        return OkData(result);
    }

    /// <summary>备份文件明细（18.3）</summary>
    [HttpGet("{backupSetId:guid}/files")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.read")]
    public async Task<ActionResult<ApiResponse<List<BackupFileDto>>>> Files(Guid backupSetId, CancellationToken ct)
    {
        var result = await _backupService.GetFilesAsync(backupSetId, ct);
        return OkData(result);
    }

    /// <summary>锁定备份（18.4，禁止保留清理）</summary>
    [HttpPost("{backupSetId:guid}/lock")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse>> Lock(
        Guid backupSetId, [FromBody] LockBackupRequest request, CancellationToken ct)
    {
        await _backupService.LockAsync(backupSetId, request, ct);
        return OkMessage("备份已锁定");
    }

    /// <summary>解锁备份（18.5）</summary>
    [HttpPost("{backupSetId:guid}/unlock")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse>> Unlock(Guid backupSetId, CancellationToken ct)
    {
        await _backupService.UnlockAsync(backupSetId, ct);
        return OkMessage("备份已解锁");
    }

    /// <summary>重新校验（18.6，异步）</summary>
    [HttpPost("{backupSetId:guid}/verify")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse<VerifyBackupResponse>>> Verify(Guid backupSetId, CancellationToken ct)
    {
        var result = await _backupService.VerifyAsync(backupSetId, ct);
        return AcceptedData(result, "已加入重校验队列");
    }
}
