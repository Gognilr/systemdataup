using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// Agent 图形化安装器的公开引导接口。
/// 只返回公开的签名公钥、Turnkey 服务端证书和发布包路径；Secure 模式仍由安装器/管理员提供注册令牌，LanSimple 模式按网络策略免令牌登记。
/// </summary>
[Route("api/v1/agent/bootstrap")]
[AllowAnonymous]
public sealed class AgentBootstrapController : ApiBaseController
{
    private readonly CommandSigner _commandSigner;
    private readonly IConfiguration _configuration;

    public AgentBootstrapController(CommandSigner commandSigner, IConfiguration configuration)
    {
        _commandSigner = commandSigner;
        _configuration = configuration;
    }

    [HttpGet]
    public ActionResult<ApiResponse<AgentBootstrapResponse>> Get()
    {
        try
        {
            var deploymentMode = _configuration["DeploymentMode"] ?? "Secure";
            string? serverCertificateFingerprint = null;
            string? serverCertificatePem = null;
            if (string.Equals(deploymentMode, "LanSimple", StringComparison.OrdinalIgnoreCase))
            {
                // LocalServerBootstrap 在进程启动时加载证书并缓存公开材料；
                // 引导接口不能为每个请求重新打开受保护的 PFX。
                serverCertificateFingerprint = _configuration["Security:ServerCertificate:Fingerprint"]
                    ?? throw new InvalidOperationException("LAN Turnkey 服务端证书指纹未缓存。");
                serverCertificatePem = _configuration["Security:ServerCertificate:CertificatePem"]
                    ?? throw new InvalidOperationException("LAN Turnkey 服务端证书 PEM 未缓存。");
            }

            return OkData(new AgentBootstrapResponse
            {
                ServerSigningPublicKey = _commandSigner.ExportPublicKey(),
                AgentPackagePath = "/downloads/BackupMonitor.Agent.zip",
                ProtocolVersion = 1,
                DeploymentMode = deploymentMode,
                AutomaticEnrollment = _configuration.GetValue("LanMode:AutomaticEnrollment", false),
                ServerCertificateFingerprint = serverCertificateFingerprint,
                ServerCertificatePem = serverCertificatePem,

                // 实例 ID 不是机密：它只是一个随机 GUID，用来回答「你还是不是原来那台」。
                // 不在这里返回的话，Agent 运行期就只能从明文 UDP 发现应答里取，
                // 而那正是要防的那条通道——锚点和被校验的对象出自同一处等于没校验。
                // Secure 模式下 Server:InstanceId 可能没配，此时返回 null，
                // 客户端会因为缺锚点而拒绝一切自动地址切换（宁可不切）。
                ServerInstanceId = _configuration["Server:InstanceId"]
            });
        }
        catch (InvalidOperationException ex)
        {
            var response = ApiResponse<AgentBootstrapResponse>.Fail(
                "AGENT_BOOTSTRAP_UNAVAILABLE",
                ex.Message);
            response.RequestId = RequestId;
            return StatusCode(StatusCodes.Status503ServiceUnavailable, response);
        }
    }
}
