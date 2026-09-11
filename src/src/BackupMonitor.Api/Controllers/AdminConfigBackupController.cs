using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 配置备份包（.bmbp）的远程入口（整改清单 2026-09-10 · R4）。
///
/// 包内是 server-secrets.json + client-ca.pfx + server-certificate.pfx + 整库转储，
/// 等于整个系统的命。此前唯一的导出入口是服务管理台上的按钮，
/// 远程管理的人完全没有入口——必须坐到服务器前面点。
/// </summary>
[Route("api/v1/admin/config-backup")]
public class AdminConfigBackupController : ApiBaseController
{
    private readonly IConfigBackupService _configBackup;

    public AdminConfigBackupController(IConfigBackupService configBackup)
    {
        _configBackup = configBackup;
    }

    /// <summary>上次导出时间与结果、是否超期。</summary>
    [HttpGet("status")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<ConfigBackupStatusDto>>> GetStatus(CancellationToken ct)
    {
        var result = await _configBackup.GetStatusAsync(ct);
        return OkData(result);
    }

    /// <summary>立即导出一份配置备份包，落到数据目录旁的固定子目录。</summary>
    [HttpPost("export")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<ConfigBackupExportDto>>> Export(CancellationToken ct)
    {
        var result = await _configBackup.ExportAsync(ct);
        return OkData(result,
            "配置备份包已导出；包内含服务端密钥与整库转储，请复制到另一台机器保存。"
            + "包内不含备份文件本体，需另行复制。");
    }

    /// <summary>
    /// 为最近一份配置备份包签发一次性下载令牌。
    /// 与恢复下载同一套口径：库里只存哈希，明文只在本响应里出现一次，用掉即失效。
    /// </summary>
    [HttpPost("download-token")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<ConfigBackupDownloadTokenDto>>> IssueDownloadToken(CancellationToken ct)
    {
        var result = await _configBackup.IssueDownloadTokenAsync(ct);
        return OkData(result, "下载令牌已签发，请在有效期内完成下载；该链接只能使用一次");
    }
}

/// <summary>
/// 配置备份包下载端点：令牌即凭据，无需 JWT（与恢复下载 /api/v1/downloads 同一形状）。
/// </summary>
[Route("api/v1/config-backup-downloads")]
public class ConfigBackupDownloadsController : ApiBaseController
{
    private readonly IConfigBackupService _configBackup;

    public ConfigBackupDownloadsController(IConfigBackupService configBackup)
    {
        _configBackup = configBackup;
    }

    [HttpGet("{downloadToken}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string downloadToken, CancellationToken ct)
    {
        var (filePath, fileName, sizeBytes) = await _configBackup.ConsumeDownloadTokenAsync(downloadToken, ct);

        Response.ContentLength = sizeBytes;
        // 明说不支持续传：令牌是一次性的，续传请求会拿着一个已核销的令牌回来，
        // 表现成「下到一半 404」。不写这个头，下载工具至少不会去试。
        Response.Headers.AcceptRanges = "none";
        return PhysicalFile(filePath, "application/octet-stream", fileName);
    }
}
