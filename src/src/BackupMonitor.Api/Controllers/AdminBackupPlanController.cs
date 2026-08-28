using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 管理端备份计划（V028）：把多个任务编成一组，到点由服务端按顺序驱动。
/// 顺序执行的推进在 SequentialExecutionWorker 里，这里只做增删改查与「立即执行一次」。
/// </summary>
[Route("api/v1/admin/backup-plans")]
public class AdminBackupPlanController : ApiBaseController
{
    private readonly IBackupPlanService _planService;
    private readonly IExecutionQueueService _queue;

    public AdminBackupPlanController(IBackupPlanService planService, IExecutionQueueService queue)
    {
        _planService = planService;
        _queue = queue;
    }

    /// <summary>计划列表</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<List<BackupPlanDto>>>> List(CancellationToken ct)
        => OkData(await _planService.GetListAsync(ct));

    /// <summary>计划详情</summary>
    [HttpGet("{planId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<BackupPlanDto>>> Get(Guid planId, CancellationToken ct)
        => OkData(await _planService.GetAsync(planId, ct));

    /// <summary>创建计划</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<BackupPlanDto>>> Create(
        [FromBody] BackupPlanUpsertDto request, CancellationToken ct)
        => OkData(await _planService.CreateAsync(request, ct), "备份计划已创建");

    /// <summary>更新计划</summary>
    [HttpPut("{planId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<BackupPlanDto>>> Update(
        Guid planId, [FromBody] BackupPlanUpsertDto request, CancellationToken ct)
        => OkData(await _planService.UpdateAsync(planId, request, ct), "备份计划已保存");

    /// <summary>删除计划（正在执行时拒绝）</summary>
    [HttpDelete("{planId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid planId, CancellationToken ct)
    {
        await _planService.DeleteAsync(planId, ct);
        return OkMessage("备份计划已删除");
    }

    /// <summary>立即执行一次（不影响下一次到点执行）</summary>
    [HttpPost("{planId:guid}/trigger")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.upload")]
    public async Task<ActionResult<ApiResponse<ExecutionRunDto>>> Trigger(Guid planId, CancellationToken ct)
        => OkData(await _planService.TriggerAsync(planId, ct), "已开始执行");

    /// <summary>该计划的历次执行</summary>
    [HttpGet("{planId:guid}/runs")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<ExecutionRunDto>>>> Runs(
        Guid planId, [FromQuery] PagedQuery query, CancellationToken ct)
        => OkData(await _queue.ListRunsAsync(planId, query, ct));
}

/// <summary>顺序执行记录（备份计划与批量上传共用同一个队列，因此也共用这组只读接口）</summary>
[Route("api/v1/admin/execution-runs")]
public class AdminExecutionRunController : ApiBaseController
{
    private readonly IExecutionQueueService _queue;

    public AdminExecutionRunController(IExecutionQueueService queue)
    {
        _queue = queue;
    }

    /// <summary>执行记录列表</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<ExecutionRunDto>>>> List(
        [FromQuery] PagedQuery query, CancellationToken ct)
        => OkData(await _queue.ListRunsAsync(null, query, ct));

    /// <summary>执行详情（含每一项的状态）</summary>
    [HttpGet("{runId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<ExecutionRunDto>>> Get(Guid runId, CancellationToken ct)
        => OkData(await _queue.GetRunAsync(runId, ct));

    /// <summary>取消：还没开跑的项一律取消，已经在跑的项让它自己跑完</summary>
    [HttpPost("{runId:guid}/cancel")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<ExecutionRunDto>>> Cancel(Guid runId, CancellationToken ct)
        => OkData(await _queue.CancelRunAsync(runId, ct), "已取消，正在跑的那几项会自己跑完");
}
