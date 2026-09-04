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

    /// <summary>
    /// 备份列表按「客户端 + 任务」归拢。筛选条件与 18.1 完全一致，
    /// 展开某一组时前端再带 taskId 调 18.1 拿明细。
    /// </summary>
    [HttpGet("groups")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<BackupSetGroupDto>>>> Groups(
        [FromQuery] BackupQuery query, CancellationToken ct)
    {
        var result = await _backupService.GetGroupsAsync(query, ct);
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

    /// <summary>
    /// 删除备份集（待办方案 D）：移入回收站而不是直接物理删，误删还能捞回来。
    /// 已锁定 / 有活动保留锁 / 正在被恢复下载的备份集会被拒绝（409），
    /// 判据与保留清理完全一致。
    /// </summary>
    [HttpPost("{backupSetId:guid}/recycle")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse>> Recycle(Guid backupSetId, CancellationToken ct)
    {
        await _backupService.RecycleAsync(backupSetId, ct);
        return OkMessage("已移入回收站，到期前都可以还原");
    }

    /// <summary>从回收站还原</summary>
    [HttpPost("{backupSetId:guid}/restore-from-recycle-bin")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse>> RestoreFromRecycleBin(Guid backupSetId, CancellationToken ct)
    {
        await _backupService.RestoreFromRecycleBinAsync(backupSetId, ct);
        return OkMessage("已从回收站还原");
    }

    /// <summary>立即彻底删除（只对回收站里的备份集开放，物理删除仓库目录，不可撤销）</summary>
    [HttpPost("{backupSetId:guid}/purge")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse>> Purge(Guid backupSetId, CancellationToken ct)
    {
        await _backupService.PurgeAsync(backupSetId, ct);
        return OkMessage("备份集已彻底删除");
    }

    /// <summary>
    /// 批量移入回收站。逐条执行、逐条报告——选中的一批里有一份被锁定，
    /// 不该让另外几十份也删不掉，也不该闷声跳过。
    /// </summary>
    [HttpPost("recycle-batch")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse<BackupSetBatchResult>>> RecycleBatch(
        [FromBody] BackupSetBatchRequest request, CancellationToken ct)
    {
        var result = await _backupService.RecycleBatchAsync(request, ct);
        return OkData(result);
    }

    /// <summary>批量从回收站还原</summary>
    [HttpPost("restore-from-recycle-bin-batch")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse<BackupSetBatchResult>>> RestoreFromRecycleBinBatch(
        [FromBody] BackupSetBatchRequest request, CancellationToken ct)
    {
        var result = await _backupService.RestoreFromRecycleBinBatchAsync(request, ct);
        return OkData(result);
    }

    /// <summary>批量彻底删除（只对回收站里的备份集开放，物理删除仓库目录，不可撤销）</summary>
    [HttpPost("purge-batch")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse<BackupSetBatchResult>>> PurgeBatch(
        [FromBody] BackupSetBatchRequest request, CancellationToken ct)
    {
        var result = await _backupService.PurgeBatchAsync(request, ct);
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

    /// <summary>隔离备份（D1：人工怀疑该版本有问题，不参与保留计算、不能用于恢复，但也不删除）</summary>
    [HttpPost("{backupSetId:guid}/quarantine")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse>> Quarantine(
        Guid backupSetId, [FromBody] QuarantineBackupRequest request, CancellationToken ct)
    {
        await _backupService.QuarantineAsync(backupSetId, request, ct);
        return OkMessage("备份已隔离");
    }

    /// <summary>解除隔离（D1）</summary>
    [HttpPost("{backupSetId:guid}/unquarantine")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse>> Unquarantine(Guid backupSetId, CancellationToken ct)
    {
        await _backupService.UnquarantineAsync(backupSetId, ct);
        return OkMessage("已解除隔离");
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
