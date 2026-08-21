using System.Diagnostics;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端部署状态摘要；不回显内部密钥、数据库连接串或安装器内部路径。</summary>
[Route("api/v1/admin/deployment-status")]
public sealed class AdminDeploymentController : ApiBaseController
{
    private readonly IConfiguration _configuration;
    private readonly HealthCheckService _healthChecks;
    private readonly IWebHostEnvironment _environment;
    private readonly LanEnrollmentService _enrollment;

    public AdminDeploymentController(
        IConfiguration configuration,
        HealthCheckService healthChecks,
        IWebHostEnvironment environment,
        LanEnrollmentService enrollment)
    {
        _configuration = configuration;
        _healthChecks = healthChecks;
        _environment = environment;
        _enrollment = enrollment;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<DeploymentStatusDto>>> Get(CancellationToken ct)
    {
        var health = await _healthChecks.CheckHealthAsync(ct);
        var advertisedUrl = _configuration["LanMode:AdvertisedUrl"];
        var serverAddress = string.IsNullOrWhiteSpace(advertisedUrl)
            ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/')
            : advertisedUrl.TrimEnd('/');

        var configuredUntil = DateTime.TryParse(
            _configuration["LanMode:EnrollmentOpenUntil"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsedUntil)
            ? parsedUntil
            : (DateTime?)null;
        var openUntil = await _enrollment.GetOpenUntilAsync(configuredUntil, ct);

        return OkData(new DeploymentStatusDto
        {
            ServerAddress = serverAddress,
            ServerStatus = "running",
            DatabaseStatus = health.Status.ToString().ToLowerInvariant(),
            ClientInstallerVersion = GetClientInstallerVersion(),
            DeploymentMode = _configuration["DeploymentMode"] ?? "Secure",
            AutomaticEnrollment = _configuration.GetValue("LanMode:AutomaticEnrollment", false),
            EnrollmentOpenUntilUtc = openUntil,
            CheckedAtUtc = DateTime.UtcNow
        });
    }

    [HttpPost("enrollment-window")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:clients.manage")]
    public async Task<ActionResult<ApiResponse<object>>> OpenEnrollmentWindow(CancellationToken ct)
    {
        var until = await _enrollment.OpenForAsync(UserId, TimeSpan.FromMinutes(30), ct);
        return OkData<object>(new { enrollmentOpenUntilUtc = until }, "LAN 自动登记已开放 30 分钟");
    }

    private string GetClientInstallerVersion()
    {
        var path = Path.Combine(
            _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"),
            "downloads",
            "BackupMonitor.Agent.Setup.exe");

        if (!System.IO.File.Exists(path))
            return "not-installed";

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
