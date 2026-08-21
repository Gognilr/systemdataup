using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

[Route("api/v1/admin/registration-tokens")]
public sealed class AdminRegistrationTokenController : ApiBaseController
{
    private readonly IRegistrationTokenService _service;

    public AdminRegistrationTokenController(IRegistrationTokenService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.read")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RegistrationTokenDto>>>> List(
        [FromQuery] RegistrationTokenQuery query, CancellationToken ct)
    {
        return OkData(await _service.ListAsync(query, ct));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<CreateRegistrationTokenResponse>>> Create(
        [FromBody] CreateRegistrationTokenRequest request, CancellationToken ct)
    {
        return OkData(await _service.CreateAsync(request, ct), "注册令牌已创建；明文令牌只在本次响应中返回");
    }

    [HttpPost("{tokenId:guid}/revoke")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse>> Revoke(
        Guid tokenId, [FromBody] RevokeRegistrationTokenRequest request, CancellationToken ct)
    {
        await _service.RevokeAsync(tokenId, request, ct);
        return OkMessage("注册令牌已撤销");
    }
}
