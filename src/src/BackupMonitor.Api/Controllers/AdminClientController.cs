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

    /// <summary>审批通过并签发证书（15.3）</summary>
    [HttpPost("{clientId:guid}/approve")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<ApproveClientResponseDto>>> Approve(Guid clientId, CancellationToken ct)
    {
        var result = await _clientService.ApproveAsync(clientId, ct);
        return OkData(result, "客户端已审批通过并签发证书");
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
