using BackupMonitor.Api.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>Agent 心跳与配置同步（设计书 11）</summary>
[Route("api/v1/agent")]
[Authorize(AuthenticationSchemes = ClientCertificateAuthenticationHandler.SchemeName)]
public class AgentSystemController : ApiBaseController
{
    private readonly IAgentHeartbeatService _heartbeatService;
    private readonly IAgentConfigService _configService;

    public AgentSystemController(IAgentHeartbeatService heartbeatService, IAgentConfigService configService)
    {
        _heartbeatService = heartbeatService;
        _configService = configService;
    }

    /// <summary>心跳上报（11.1）</summary>
    [HttpPost("heartbeat")]
    public async Task<ActionResult<ApiResponse<HeartbeatResponse>>> Heartbeat(
        [FromBody] HeartbeatRequest request, CancellationToken ct)
    {
        var agentVersion = Request.Headers["X-Agent-Version"].FirstOrDefault();
        var result = await _heartbeatService.ProcessAsync(ClientIdentity, request, agentVersion, ct);
        return OkData(result);
    }

    /// <summary>配置同步（11.2，客户端携带当前版本号，已是最新时 data 为空）</summary>
    [HttpGet("config")]
    public async Task<ActionResult<ApiResponse<AgentConfigResponse>>> GetConfig(
        [FromQuery] long currentVersion, CancellationToken ct)
    {
        var requiredVersion = await _configService.GetRequiredVersionAsync(ClientIdentity, ct);
        if (currentVersion >= requiredVersion)
        {
            var response = new ApiResponse<AgentConfigResponse>
            {
                Success = true,
                Data = null,
                Message = "config_unchanged",
                RequestId = RequestId
            };
            return Ok(response);
        }

        var config = await _configService.GetConfigAsync(ClientIdentity, currentVersion, ct);
        return OkData(config);
    }
}
