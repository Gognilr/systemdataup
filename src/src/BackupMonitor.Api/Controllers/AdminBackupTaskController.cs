using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端备份任务管理（设计书 16）</summary>
[Route("api/v1/admin/backup-tasks")]
public class AdminBackupTaskController : ApiBaseController
{
    private readonly IBackupTaskService _taskService;

    public AdminBackupTaskController(IBackupTaskService taskService)
    {
        _taskService = taskService;
    }

    /// <summary>创建备份任务（16.1）</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<BackupTaskDetailDto>>> Create(
        [FromBody] CreateBackupTaskRequest request, CancellationToken ct)
    {
        var result = await _taskService.CreateAsync(request, ct);
        return OkData(result, "任务已创建");
    }

    /// <summary>任务列表（16.4）</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<BackupTaskListItemDto>>>> List(
        [FromQuery] BackupTaskQuery query, CancellationToken ct)
    {
        var result = await _taskService.GetListAsync(query, ct);
        return OkData(result);
    }

    /// <summary>任务详情</summary>
    [HttpGet("{taskId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<BackupTaskDetailDto>>> Detail(Guid taskId, CancellationToken ct)
    {
        var result = await _taskService.GetDetailAsync(taskId, ct);
        return OkData(result);
    }

    /// <summary>修改备份任务（16.2，必须携带 rowVersion）</summary>
    [HttpPut("{taskId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<BackupTaskDetailDto>>> Update(
        Guid taskId, [FromBody] UpdateBackupTaskRequest request, CancellationToken ct)
    {
        var result = await _taskService.UpdateAsync(taskId, request, ct);
        return OkData(result, "任务已更新");
    }

    /// <summary>删除备份任务（16.3，已有备份数据时禁止）</summary>
    [HttpDelete("{taskId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid taskId, CancellationToken ct)
    {
        await _taskService.DeleteAsync(taskId, ct);
        return OkMessage("任务已删除");
    }

    /// <summary>暂停任务（16.5）</summary>
    [HttpPost("{taskId:guid}/pause")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<BackupTaskDetailDto>>> Pause(Guid taskId, CancellationToken ct)
    {
        var result = await _taskService.PauseAsync(taskId, ct);
        return OkData(result, "任务已暂停");
    }

    /// <summary>恢复任务（16.6，还原暂停前模式）</summary>
    [HttpPost("{taskId:guid}/resume")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<BackupTaskDetailDto>>> Resume(Guid taskId, CancellationToken ct)
    {
        var result = await _taskService.ResumeAsync(taskId, ct);
        return OkData(result, "任务已恢复");
    }

    /// <summary>手动下发预检指令</summary>
    [HttpPost("{taskId:guid}/precheck")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.precheck")]
    public async Task<ActionResult<ApiResponse<DispatchCommandResponse>>> DispatchPrecheck(Guid taskId, CancellationToken ct)
    {
        var result = await _taskService.DispatchPrecheckAsync(taskId, ct);
        return OkData(result, "预检指令已下发");
    }

    /// <summary>下发上传指令（16.7，候选审批通过后调用）</summary>
    [HttpPost("{taskId:guid}/upload")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.upload")]
    public async Task<ActionResult<ApiResponse<DispatchCommandResponse>>> DispatchUpload(
        Guid taskId, [FromBody] DispatchUploadRequest request, CancellationToken ct)
    {
        var result = await _taskService.DispatchUploadAsync(taskId, request, ct);
        return OkData(result, "上传指令已下发");
    }

    /// <summary>识别测试（16.8，异步）</summary>
    [HttpPost("{taskId:guid}/test-recognition")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.manage")]
    public async Task<ActionResult<ApiResponse<TestRecognitionResponse>>> TestRecognition(Guid taskId, CancellationToken ct)
    {
        var result = await _taskService.TestRecognitionAsync(taskId, ct);
        return AcceptedData(result, "识别测试已下发");
    }

    /// <summary>
    /// 最近一次识别测试的结果。等待窗口只有一分钟，而关掉对话框之后客户端照样会把它跑完——
    /// 这个接口让那次结果还能被找回来，而不是只能重测一遍。从未测过时 data 为 null。
    /// </summary>
    [HttpGet("{taskId:guid}/test-recognition/latest")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<CommandResultDto?>>> LatestTestRecognition(Guid taskId, CancellationToken ct)
    {
        var result = await _taskService.GetLatestRecognitionTestAsync(taskId, ct);
        return OkData(result);
    }

    /// <summary>
    /// 查询识别测试（或任意指令）的执行结果。
    /// 识别测试是异步的：指令下发后要等 Agent 领走并执行，界面据此轮询。
    /// </summary>
    [HttpGet("commands/{commandId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:tasks.read")]
    public async Task<ActionResult<ApiResponse<CommandResultDto>>> GetCommandResult(Guid commandId, CancellationToken ct)
    {
        var result = await _taskService.GetCommandResultAsync(commandId, ct);
        return OkData(result);
    }
}
