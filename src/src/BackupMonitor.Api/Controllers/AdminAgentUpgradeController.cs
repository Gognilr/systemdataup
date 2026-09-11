using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 管理端 Agent 升级（经 upgrade_agent 指令通道下发）。
///
/// R20 之后这里不再是「发出去就完事」：一次下发是一条有生命周期的记录，
/// 分批放行、按客户端上报的版本号判成功，界面要能看到每一批的结果。
/// </summary>
[Route("api/v1/admin/agent-upgrades")]
public class AdminAgentUpgradeController : ApiBaseController
{
    private readonly IAgentUpgradeService _upgradeService;
    private readonly IConfiguration _configuration;

    public AdminAgentUpgradeController(IAgentUpgradeService upgradeService, IConfiguration configuration)
    {
        _upgradeService = upgradeService;
        _configuration = configuration;
    }

    /// <summary>创建一次分批升级下发（Idempotency-Key 头保证逐客户端幂等，逐项返回第一批结果）</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<UpgradeAgentResponseDto>>> Dispatch(
        [FromBody] UpgradeAgentRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var result = await _upgradeService.DispatchAsync(request, idempotencyKey, ct);
        return OkData(result,
            $"第一批已下发 {result.Dispatched} 台，失败 {result.Failed} 台；后续批次要等这一批心跳回来且版本号变了才会放行");
    }

    /// <summary>升级下发列表（新的在前）</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<List<AgentUpgradeSummaryDto>>>> List(
        [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        return OkData(await _upgradeService.ListAsync(limit, ct));
    }

    /// <summary>
    /// 下发表单的自动填充信息。
    ///
    /// 手抄一个 64 位十六进制哈希本身就是个故障源：抄错一位的表现是
    /// 每台机器都下载成功、校验失败，而那时人只会怀疑包坏了。
    /// </summary>
    [HttpGet("package")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<AgentUpgradePackageInfoDto>>> Package(CancellationToken ct)
    {
        // 地址优先用服务端**对外公告**的那个（LanMode:AdvertisedUrl）——
        // 客户端就是按它连过来的，也只认它。
        //
        // 原先按这次请求的来源拼，而「管理员从哪个地址打开管理页面」和
        // 「客户端被配置成连哪个地址」是两件独立的事：管理员习惯敲 IP
        // （https://172.16.11.130:5080），客户端却是局域网发现来的、记的是主机名。
        // Agent 那一侧只比 host:port 字符串，对不上就回 UPGRADE_PACKAGE_URL_FORBIDDEN，
        // 而界面上看到的只是「一直没成功」——两个地址都指向同一台机器，
        // 没人会怀疑是这里差了一个写法。
        //
        // 公告地址取不到（Secure 形态由 nginx 终结、没有这个配置）时退回请求来源。
        var advertised = _configuration["LanMode:AdvertisedUrl"];
        var baseUrl = Uri.TryCreate(advertised, UriKind.Absolute, out var advertisedUri)
            ? advertisedUri.GetLeftPart(UriPartial.Authority)
            : $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        return OkData(await _upgradeService.GetPackageInfoAsync(baseUrl, ct));
    }

    /// <summary>单次下发的逐机器详情</summary>
    [HttpGet("{upgradeId:guid}")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<AgentUpgradeDetailDto>>> Get(Guid upgradeId, CancellationToken ct)
    {
        return OkData(await _upgradeService.GetAsync(upgradeId, ct));
    }

    /// <summary>取消尚未下发的批次（已经发下去的那批拦不住）</summary>
    [HttpPost("{upgradeId:guid}/cancel")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<AgentUpgradeDetailDto>>> Cancel(Guid upgradeId, CancellationToken ct)
    {
        var result = await _upgradeService.CancelAsync(upgradeId, ct);
        return OkData(result, "已停止后续批次；已经下发出去的指令无法撤回");
    }
}
