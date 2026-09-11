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
    private readonly IClientAdminService _clientService;
    private readonly IAgentUpgradeService _upgradeService;

    public AgentSystemController(
        IAgentHeartbeatService heartbeatService,
        IAgentConfigService configService,
        IClientAdminService clientService,
        IAgentUpgradeService upgradeService)
    {
        _heartbeatService = heartbeatService;
        _configService = configService;
        _clientService = clientService;
        _upgradeService = upgradeService;
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

    /// <summary>
    /// 升级完成（或回滚）后的主动回报（R20）。
    ///
    /// 这是整条升级链路上唯一由**新进程**发出的一句话，也是判「真的换过去了」的依据之一。
    /// 旧实现拿「指令执行完了」当成功，而那时换文件的动作根本还没发生。
    /// </summary>
    [HttpPost("upgrade-result")]
    public async Task<ActionResult<ApiResponse>> ReportUpgradeResult(
        [FromBody] AgentUpgradeResultRequest request, CancellationToken ct)
    {
        await _upgradeService.ReportResultAsync(ClientIdentity, request, ct);
        return OkMessage("已记录升级结果");
    }

    /// <summary>客户端证书续签；旧证书在新证书生效后保留七天重叠期。</summary>
    [HttpPost("certificate/renew")]
    public async Task<ActionResult<ApiResponse<CertificateRenewalResponse>>> RenewCertificate(CancellationToken ct)
    {
        var result = await _clientService.RenewCertificateAsync(ClientIdentity, ct);
        return OkData(result);
    }
}
