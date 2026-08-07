using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>客户端注册（设计书 10，注册阶段无需证书认证）</summary>
[Route("api/v1/agent/registrations")]
[AllowAnonymous]
public class AgentRegistrationController : ApiBaseController
{
    private readonly IAgentRegistrationService _registrationService;

    public AgentRegistrationController(IAgentRegistrationService registrationService)
    {
        _registrationService = registrationService;
    }

    /// <summary>提交注册（10.1）</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<SubmitRegistrationResponse>>> Submit(
        [FromBody] SubmitRegistrationRequest request, CancellationToken ct)
    {
        var result = await _registrationService.SubmitAsync(request, ct);
        return OkData(result);
    }

    /// <summary>查询注册结果（10.2，审批通过后返回签发的证书）</summary>
    [HttpGet("{registrationId:guid}")]
    public async Task<ActionResult<ApiResponse<RegistrationResultResponse>>> GetResult(
        Guid registrationId, CancellationToken ct)
    {
        var result = await _registrationService.GetResultAsync(registrationId, ct);
        return OkData(result);
    }
}
