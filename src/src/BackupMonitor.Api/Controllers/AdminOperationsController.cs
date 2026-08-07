using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端批量操作（设计书 17）</summary>
[Route("api/v1/admin/operations")]
public class AdminOperationsController : ApiBaseController
{
    private readonly IBatchOperationService _batchService;

    public AdminOperationsController(IBatchOperationService batchService)
    {
        _batchService = batchService;
    }

    /// <summary>创建批量预检（17.1）</summary>
    [HttpPost("precheck-batches")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:operations.batch")]
    public async Task<ActionResult<ApiResponse<CreatePrecheckBatchResponse>>> CreatePrecheckBatch(
        [FromBody] CreatePrecheckBatchRequest request, CancellationToken ct)
    {
        var result = await _batchService.CreatePrecheckBatchAsync(request, ct);
        return OkData(result, $"已下发 {result.DispatchedCommands} 条预检指令");
    }

    /// <summary>创建批量上传（17.2，Idempotency-Key 头保证幂等）</summary>
    [HttpPost("upload-batches")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:operations.batch")]
    public async Task<ActionResult<ApiResponse<UploadBatchDto>>> CreateUploadBatch(
        [FromBody] CreateUploadBatchRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var result = await _batchService.CreateUploadBatchAsync(request, idempotencyKey, ct);
        return OkData(result, "批量上传已创建");
    }

    /// <summary>批量上传批次列表（17.3）</summary>
    [HttpGet("upload-batches")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:operations.batch")]
    public async Task<ActionResult<ApiResponse<PagedResult<UploadBatchDto>>>> ListBatches(
        [FromQuery] PagedQuery query, CancellationToken ct)
    {
        var result = await _batchService.ListBatchesAsync(query, ct);
        return OkData(result);
    }

    /// <summary>批量上传批次详情（含单项指令/会话状态）</summary>
    [HttpGet("upload-batches/{batchId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:operations.batch")]
    public async Task<ActionResult<ApiResponse<UploadBatchDto>>> BatchDetail(Guid batchId, CancellationToken ct)
    {
        var result = await _batchService.GetBatchAsync(batchId, ct);
        return OkData(result);
    }
}
