using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 业务系统探测（V047）。
///
/// 与被监控的 Windows 服务分工：那个回答「进程在不在」，这个回答「业务用不用得了」。
/// 两者不能互相替代——这类系统最常见的故障形态恰恰是**进程好好的、业务已经用不了**。
/// </summary>
[Route("api/v1/admin/monitored-endpoints")]
public class AdminMonitoredEndpointController : ApiBaseController
{
    private readonly IMonitoredEndpointService _endpoints;

    public AdminMonitoredEndpointController(IMonitoredEndpointService endpoints)
    {
        _endpoints = endpoints;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.read")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MonitoredEndpointDto>>>> List(CancellationToken ct)
        => OkData(await _endpoints.ListAsync(ct));

    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<MonitoredEndpointDto>>> Create(
        [FromBody] MonitoredEndpointUpsertDto request, CancellationToken ct)
        => OkData(await _endpoints.CreateAsync(request, ct), "探测已创建");

    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<MonitoredEndpointDto>>> Update(
        Guid id, [FromBody] MonitoredEndpointUpsertDto request, CancellationToken ct)
        => OkData(await _endpoints.UpdateAsync(id, request, ct), "探测已保存");

    [HttpDelete("{id:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid id, CancellationToken ct)
    {
        await _endpoints.DeleteAsync(id, ct);
        return OkMessage("探测已删除");
    }

    /// <summary>
    /// 立即试一次，不落库。
    ///
    /// 这个接口主要是为了 HTTP 探测里那项「期望包含文本」——它是整个功能真正管用的一项，
    /// 也是最容易配错的一项：填的词如果在错误页里也出现，这条探测就永远是绿的，
    /// 而人以为它在把关。回显真实响应的开头若干字符，照着挑词，比凭猜靠谱得多。
    /// </summary>
    [HttpPost("try")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<EndpointProbeResultDto>>> Try(
        [FromBody] MonitoredEndpointUpsertDto request, CancellationToken ct)
        => OkData(await _endpoints.TryProbeAsync(request, ct));
}
