using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端审计日志（设计书 21，只读）</summary>
[Route("api/v1/admin/audit-logs")]
public class AdminAuditController : ApiBaseController
{
    private readonly IAuditQueryService _auditService;

    public AdminAuditController(IAuditQueryService auditService)
    {
        _auditService = auditService;
    }

    /// <summary>审计日志查询（21.1，只追加不可修改）</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:audit.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<AuditLogDto>>>> List(
        [FromQuery] AuditLogQuery query, CancellationToken ct)
    {
        var result = await _auditService.GetListAsync(query, ct);
        return OkData(result);
    }
}
