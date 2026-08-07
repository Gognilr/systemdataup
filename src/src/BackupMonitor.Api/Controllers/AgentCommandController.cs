using BackupMonitor.Api.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>Agent 指令领取与回报（设计书 12）</summary>
[Route("api/v1/agent/commands")]
[Authorize(AuthenticationSchemes = ClientCertificateAuthenticationHandler.SchemeName)]
public class AgentCommandController : ApiBaseController
{
    private readonly IAgentCommandService _commandService;

    public AgentCommandController(IAgentCommandService commandService)
    {
        _commandService = commandService;
    }

    /// <summary>领取待执行指令（12.1，服务端原子认领）</summary>
    [HttpPost("claim")]
    public async Task<ActionResult<ApiResponse<ClaimCommandsResponse>>> Claim(
        [FromBody] ClaimCommandsRequest request, CancellationToken ct)
    {
        var result = await _commandService.ClaimAsync(ClientIdentity, request, ct);
        return OkData(result);
    }

    /// <summary>上报指令开始执行（12.2）</summary>
    [HttpPost("{commandId:guid}/started")]
    public async Task<ActionResult<ApiResponse>> Started(Guid commandId, CancellationToken ct)
    {
        await _commandService.ReportStartedAsync(ClientIdentity, commandId, ct);
        return OkMessage();
    }

    /// <summary>上报指令执行进度（12.3）</summary>
    [HttpPost("{commandId:guid}/progress")]
    public async Task<ActionResult<ApiResponse>> Progress(
        Guid commandId, [FromBody] CommandProgressRequest request, CancellationToken ct)
    {
        await _commandService.ReportProgressAsync(ClientIdentity, commandId, request, ct);
        return OkMessage();
    }

    /// <summary>上报指令执行完成（12.4，幂等）</summary>
    [HttpPost("{commandId:guid}/completed")]
    public async Task<ActionResult<ApiResponse>> Completed(
        Guid commandId, [FromBody] CommandCompletedRequest request, CancellationToken ct)
    {
        await _commandService.ReportCompletedAsync(ClientIdentity, commandId, request, ct);
        return OkMessage();
    }
}
