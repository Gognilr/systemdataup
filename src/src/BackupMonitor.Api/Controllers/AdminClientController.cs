using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端客户端管理（设计书 15）</summary>
[Route("api/v1/admin/clients")]
public class AdminClientController : ApiBaseController
{
    private readonly IClientAdminService _clientService;

    public AdminClientController(IClientAdminService clientService)
    {
        _clientService = clientService;
    }

    /// <summary>客户端列表（15.1）</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<ClientListItemDto>>>> List(
        [FromQuery] ClientQuery query, CancellationToken ct)
    {
        var result = await _clientService.GetListAsync(query, ct);
        return OkData(result);
    }

    /// <summary>客户端详情（15.2）</summary>
    [HttpGet("{clientId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<ClientDetailDto>>> Detail(Guid clientId, CancellationToken ct)
    {
        var result = await _clientService.GetDetailAsync(clientId, ct);
        return OkData(result);
    }

    /// <summary>
    /// 修改客户端的显示名称与所属分组。
    ///
    /// 用 PATCH 而不是 PUT：请求体里两个字段都可以不传，语义是"没提到的不动"。
    /// 换成 PUT 就得让调用方把整个客户端对象回传一遍，而这个对象里绝大多数字段
    /// （证书、心跳、状态）根本不该由界面决定。
    /// </summary>
    [HttpPatch("{clientId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<ClientDetailDto>>> Update(
        Guid clientId, [FromBody] UpdateClientRequest request, CancellationToken ct)
    {
        var result = await _clientService.UpdateAsync(clientId, request, ct);
        return OkData(result, "客户端信息已更新");
    }

    /// <summary>客户端分组选项，供"改分组"下拉框使用。</summary>
    [HttpGet("groups")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ClientGroupOptionDto>>>> Groups(CancellationToken ct)
    {
        var result = await _clientService.GetGroupOptionsAsync(ct);
        return OkData(result);
    }

    /// <summary>最近自动登记客户端，供待办页追踪局域网免令牌登记。</summary>
    [HttpGet("recent-auto-enrollments")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RecentAutoEnrollmentDto>>>> RecentAutomaticEnrollments(
        [FromQuery] int days = 7, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var result = await _clientService.GetRecentAutomaticEnrollmentsAsync(days, limit, ct);
        return OkData(result);
    }

    /// <summary>
    /// 概览页用的在线客户端资源快照。
    /// 单独一个接口而不是给列表接口加参数：列表按分页/排序/筛选组织，
    /// 而这里要的是"最需要关注的排在最前"的固定视角。
    /// </summary>
    [HttpGet("resource-overview")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ClientResourceRowDto>>>> ResourceOverview(
        [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var result = await _clientService.GetResourceOverviewAsync(limit, ct);
        return OkData(result);
    }

    /// <summary>客户端资源指标历史（默认最近 24 小时）</summary>
    [HttpGet("{clientId:guid}/metrics")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<ClientMetricsHistoryDto>>> Metrics(
        Guid clientId,
        [FromQuery] int hours = 24,
        [FromQuery] int limit = 1440,
        CancellationToken ct = default)
    {
        var result = await _clientService.GetMetricsHistoryAsync(clientId, hours, limit, ct);
        return OkData(result);
    }

    /// <summary>审批通过并签发证书（15.3）</summary>
    [HttpPost("{clientId:guid}/approve")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<ApproveClientResponseDto>>> Approve(Guid clientId, CancellationToken ct)
    {
        var result = await _clientService.ApproveAsync(clientId, ct);
        return OkData(result, "客户端已审批通过并签发证书");
    }

    /// <summary>
    /// 确认一次自动登记：把它从待办的「最近自动登记」里摘掉。
    /// 不改客户端状态，因此要的是 manage 权限而不是审批那一套流程。
    /// </summary>
    [HttpPost("{clientId:guid}/confirm-enrollment")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse>> ConfirmEnrollment(Guid clientId, CancellationToken ct)
    {
        await _clientService.ConfirmEnrollmentAsync(clientId, ct);
        return OkMessage("已确认这台机器的自动登记");
    }

    /// <summary>拒绝注册（15.3）</summary>
    [HttpPost("{clientId:guid}/reject")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse>> Reject(
        Guid clientId, [FromBody] DisableClientRequest request, CancellationToken ct)
    {
        await _clientService.RejectAsync(clientId, request.Reason, ct);
        return OkMessage("注册已拒绝");
    }

    /// <summary>禁用客户端（15.4）</summary>
    [HttpPost("{clientId:guid}/disable")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse>> Disable(
        Guid clientId, [FromBody] DisableClientRequest request, CancellationToken ct)
    {
        await _clientService.DisableAsync(clientId, request, ct);
        return OkMessage("客户端已禁用");
    }

    /// <summary>重新启用被禁用的客户端（禁用的逆操作；已注销不可恢复）</summary>
    [HttpPost("{clientId:guid}/enable")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse>> Enable(
        Guid clientId, [FromBody] EnableClientRequest request, CancellationToken ct)
    {
        await _clientService.EnableAsync(clientId, request, ct);
        return OkMessage("客户端已启用");
    }

    /// <summary>注销客户端（15.5，吊销全部证书，不可恢复）</summary>
    [HttpPost("{clientId:guid}/revoke")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse>> Revoke(
        Guid clientId, [FromBody] RevokeClientRequest request, CancellationToken ct)
    {
        await _clientService.RevokeAsync(clientId, request, ct);
        return OkMessage("客户端已注销");
    }

    /// <summary>下发刷新主机状态指令（15.6）</summary>
    [HttpPost("{clientId:guid}/refresh-metrics")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<DispatchCommandResponse>>> RefreshMetrics(Guid clientId, CancellationToken ct)
    {
        var result = await _clientService.RefreshMetricsAsync(clientId, ct);
        return OkData(result, "指令已下发");
    }
}
