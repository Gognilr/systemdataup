using BackupMonitor.Api.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>Agent 预检结果上报（设计书 13.1）</summary>
[Route("api/v1/agent/tasks")]
[Authorize(AuthenticationSchemes = ClientCertificateAuthenticationHandler.SchemeName)]
public class AgentPrecheckController : ApiBaseController
{
    private readonly IAgentPrecheckService _precheckService;

    public AgentPrecheckController(IAgentPrecheckService precheckService)
    {
        _precheckService = precheckService;
    }

    /// <summary>提交任务预检结果（幂等：同一指令重复上报返回原结果）</summary>
    [HttpPost("{taskId:guid}/precheck-results")]
    public async Task<ActionResult<ApiResponse<SubmitPrecheckResultResponse>>> SubmitResult(
        Guid taskId, [FromBody] SubmitPrecheckResultRequest request, CancellationToken ct)
    {
        var result = await _precheckService.SubmitResultAsync(ClientIdentity, taskId, request, ct);
        return OkData(result);
    }
}
