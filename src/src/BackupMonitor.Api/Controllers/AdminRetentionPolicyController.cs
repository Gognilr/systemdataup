using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端保留策略（第二批补充设计：设计书仅定义数据表，接口为补充；保留清理由定时任务执行，见设计书 25）</summary>
[Route("api/v1/admin/retention-policies")]
public class AdminRetentionPolicyController : ApiBaseController
{
    private readonly IRetentionPolicyService _retentionService;

    public AdminRetentionPolicyController(IRetentionPolicyService retentionService)
    {
        _retentionService = retentionService;
    }

    /// <summary>保留策略列表</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.read")]
    public async Task<ActionResult<ApiResponse<List<RetentionPolicyDto>>>> List(CancellationToken ct)
    {
        var result = await _retentionService.GetListAsync(ct);
        return OkData(result);
    }

    /// <summary>保留策略详情</summary>
    [HttpGet("{policyId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.read")]
    public async Task<ActionResult<ApiResponse<RetentionPolicyDto>>> Get(Guid policyId, CancellationToken ct)
    {
        var result = await _retentionService.GetAsync(policyId, ct);
        return OkData(result);
    }

    /// <summary>创建保留策略</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse<RetentionPolicyDto>>> Create(
        [FromBody] RetentionPolicyUpsertDto request, CancellationToken ct)
    {
        var result = await _retentionService.CreateAsync(request, ct);
        return OkData(result, "保留策略已创建");
    }

    /// <summary>更新保留策略</summary>
    [HttpPut("{policyId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse<RetentionPolicyDto>>> Update(
        Guid policyId, [FromBody] RetentionPolicyUpsertDto request, CancellationToken ct)
    {
        var result = await _retentionService.UpdateAsync(policyId, request, ct);
        return OkData(result, "保留策略已更新");
    }

    /// <summary>删除保留策略（有任务引用时禁止）</summary>
    [HttpDelete("{policyId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid policyId, CancellationToken ct)
    {
        await _retentionService.DeleteAsync(policyId, ct);
        return OkMessage("保留策略已删除");
    }

    /// <summary>
    /// 设为「新建任务默认」策略。
    ///
    /// 权限与策略的增删改同级（perm:backups.manage）：能改策略内容的人本来就能改到同一批备份的存亡，
    /// 再为这一个动作单开一项权限没有意义。
    /// 没有反向的「清除默认」端点——理由见 RetentionPolicyService.SetDefaultAsync。
    /// </summary>
    [HttpPost("{policyId:guid}/set-default")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:backups.manage")]
    public async Task<ActionResult<ApiResponse<RetentionPolicyDto>>> SetDefault(Guid policyId, CancellationToken ct)
    {
        var result = await _retentionService.SetDefaultAsync(policyId, ct);
        return OkData(result, "已设为新建任务默认策略");
    }
}
