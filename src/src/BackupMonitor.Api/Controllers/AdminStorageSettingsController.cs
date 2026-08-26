using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 管理端存储设置：备份最终存放目录 + 上传暂存目录。
///
/// 这两个路径以前只能在服务端安装时选一次，装完想换就得直接改 system_settings 表——
/// 管理界面上既看不到当前用的是哪个目录，也没法改。
/// </summary>
[Route("api/v1/admin")]
public class AdminStorageSettingsController : ApiBaseController
{
    private readonly IStorageSettingsService _storageSettings;

    public AdminStorageSettingsController(IStorageSettingsService storageSettings)
    {
        _storageSettings = storageSettings;
    }

    /// <summary>读取存储设置（含实际生效路径、取值来源、可写性与剩余空间）</summary>
    [HttpGet("storage-settings")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<StorageSettingsDto>>> Get(CancellationToken ct)
    {
        var result = await _storageSettings.GetAsync(ct);
        return OkData(result);
    }

    /// <summary>
    /// 浏览服务端本机目录，给存储设置的目录选择器用。
    /// 不传 path 返回磁盘列表；只返回目录，不返回文件。
    /// </summary>
    [HttpGet("storage-settings/browse")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<StorageBrowseResponseDto>>> Browse(
        [FromQuery] string? path, CancellationToken ct)
    {
        var result = await _storageSettings.BrowseAsync(path, ct);
        return OkData(result);
    }

    /// <summary>更新存储设置。留空表示清除该项配置，回落到 appsettings / 程序目录默认值</summary>
    [HttpPut("storage-settings")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<StorageSettingsDto>>> Update(
        [FromBody] UpdateStorageSettingsRequest request, CancellationToken ct)
    {
        var result = await _storageSettings.UpdateAsync(request, ct);
        return OkData(result, "存储设置已保存，新的备份会写入新目录；已入库的备份仍留在原目录");
    }
}
