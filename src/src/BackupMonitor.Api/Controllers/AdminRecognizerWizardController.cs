using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 建任务向导：浏览客户端目录 → 推断识别规则 → 预演判定结果。
///
/// 权限一律用 tasks.manage 而不是新增一个权限项：能建任务的人本来就能让这台客户端
/// 去扫任意路径，浏览目录没有扩大他的权限边界，只是让他不必凭记忆敲路径。
/// 反过来，只有 tasks.read 的人不该能枚举别人机器上的文件名。
/// </summary>
[Route("api/v1/admin")]
public class AdminRecognizerWizardController : ApiBaseController
{
    private readonly IRecognizerWizardService _wizard;

    public AdminRecognizerWizardController(IRecognizerWizardService wizard)
    {
        _wizard = wizard;
    }

    /// <summary>
    /// 下发目录浏览指令（异步：Agent 每 10 秒领一次指令）。
    /// 请求体的 path 留空表示"列出该客户端的固定磁盘"，作为浏览起点。
    /// </summary>
    [HttpPost("clients/{clientId:guid}/browse")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<DispatchCommandResponse>>> Browse(
        Guid clientId, [FromBody] BrowseClientPathRequest request, CancellationToken ct)
    {
        var result = await _wizard.BrowseAsync(clientId, request, ct);
        return AcceptedData(result, "浏览指令已下发");
    }

    /// <summary>轮询浏览结果。</summary>
    [HttpGet("clients/browse/{commandId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<BrowseClientPathResultDto>>> BrowseResult(
        Guid commandId, CancellationToken ct)
    {
        var result = await _wizard.GetBrowseResultAsync(commandId, ct);
        return OkData(result);
    }

    /// <summary>在快照上推断识别规则，并给出按该规则跑出来的预演结果。</summary>
    [HttpPost("recognizer/infer")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<RecognizerProposalDto>>> Infer(
        [FromBody] InferRecognizerRequest request, CancellationToken ct)
    {
        var result = await _wizard.InferAsync(request, ct);
        return OkData(result);
    }

    /// <summary>拿一条具体规则在快照上预演。界面上改一个勾选就调一次，代价是毫秒级。</summary>
    [HttpPost("recognizer/preview")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<RecognizerPreviewDto>>> Preview(
        [FromBody] PreviewRecognizerRequest request, CancellationToken ct)
    {
        var result = await _wizard.PreviewAsync(request, ct);
        return OkData(result);
    }
}
