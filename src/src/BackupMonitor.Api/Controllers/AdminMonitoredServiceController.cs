using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 关键 Windows 服务监控的配置接口。
///
/// 权限用 clients.manage：这是在改一台客户端的配置，跟审批、禁用同一类动作。
/// </summary>
[Route("api/v1/admin/clients/{clientId:guid}/monitored-services")]
public class AdminMonitoredServiceController : ApiBaseController
{
    private readonly IMonitoredServiceAdminService _services;

    public AdminMonitoredServiceController(IMonitoredServiceAdminService services)
    {
        _services = services;
    }

    /// <summary>该客户端已配置的关键服务。</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ClientServiceDto>>>> List(
        Guid clientId, CancellationToken ct)
    {
        var result = await _services.ListAsync(clientId, ct);
        return OkData(result);
    }

    /// <summary>新增一条关键服务监控。</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<ClientServiceDto>>> Create(
        Guid clientId, [FromBody] CreateMonitoredServiceRequest request, CancellationToken ct)
    {
        var result = await _services.CreateAsync(clientId, request, ct);
        return OkData(result, "已加入监控");
    }

    /// <summary>修改一条关键服务监控。</summary>
    [HttpPut("{definitionId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<ClientServiceDto>>> Update(
        Guid clientId, Guid definitionId, [FromBody] UpdateMonitoredServiceRequest request, CancellationToken ct)
    {
        var result = await _services.UpdateAsync(clientId, definitionId, request, ct);
        return OkData(result, "已保存");
    }

    /// <summary>移除一条关键服务监控。</summary>
    [HttpDelete("{definitionId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid clientId, Guid definitionId, CancellationToken ct)
    {
        await _services.DeleteAsync(clientId, definitionId, ct);
        return OkMessage("已移出监控");
    }

    /// <summary>
    /// 下发指令，读取这台机器上实际安装的服务列表（异步：Agent 每 10 秒领一次指令）。
    /// 让人从真实列表里挑，而不是凭记忆敲 MSSQL$SQLEXPRESS 这类服务名。
    /// </summary>
    [HttpPost("discover")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<DispatchCommandResponse>>> Discover(Guid clientId, CancellationToken ct)
    {
        var result = await _services.DispatchListServicesAsync(clientId, ct);
        return AcceptedData(result, "已下发服务列表读取指令");
    }

    /// <summary>轮询服务列表读取结果。</summary>
    [HttpGet("discover/{commandId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<ListServicesResultDto>>> DiscoverResult(
        Guid clientId, Guid commandId, CancellationToken ct)
    {
        var result = await _services.GetListServicesResultAsync(commandId, ct);
        return OkData(result);
    }
}
