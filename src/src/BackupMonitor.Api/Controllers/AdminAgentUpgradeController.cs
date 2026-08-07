using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端 Agent 升级（第二批补充设计：经 upgrade_agent 指令通道下发，无独立数据表）</summary>
[Route("api/v1/admin/agent-upgrades")]
public class AdminAgentUpgradeController : ApiBaseController
{
    private readonly IAgentUpgradeService _upgradeService;

    public AdminAgentUpgradeController(IAgentUpgradeService upgradeService)
    {
        _upgradeService = upgradeService;
    }

    /// <summary>批量下发升级指令（Idempotency-Key 头保证逐客户端幂等，逐项返回结果）</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<UpgradeAgentResponseDto>>> Dispatch(
        [FromBody] UpgradeAgentRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var result = await _upgradeService.DispatchAsync(request, idempotencyKey, ct);
        return OkData(result, $"已下发 {result.Dispatched} 条升级指令，失败 {result.Failed}");
    }
}
