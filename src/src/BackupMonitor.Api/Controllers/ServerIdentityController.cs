using System.Reflection;
using BackupMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 登录前可见的服务端身份（实施方案 U4）。
///
/// 只回答一个问题：**我连的是不是那台服务器**。LAN Turnkey 形态下管理员本来就要做这件事
/// ——Agent 安装器里的指纹核对是同一件事的机器版——但管理网页此前没有任何地方能看到指纹。
///
/// 刻意**不**复用 /api/v1/agent/bootstrap：那个接口还返回签名公钥、证书 PEM 与发布包路径，
/// 而且 Secure 模式下会 503。登录页只需要「指纹 + 版本」，多返回一个字段都是多余的暴露面。
///
/// 这里返回的两项都不是机密：证书指纹在每一次 TLS 握手里都会公开出示，
/// 版本号是产品信息。除此之外不加主机名、不加内网地址、不加客户端数量——
/// 那些是登录之后才该看到的东西。
/// </summary>
[Route("api/v1/public/server-identity")]
[AllowAnonymous]
public sealed class ServerIdentityController : ApiBaseController
{
    private readonly IConfiguration _configuration;

    public ServerIdentityController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public ActionResult<ApiResponse<ServerIdentityResponse>> Get()
    {
        // Secure 形态下 TLS 由 nginx 终结，服务端进程手里没有那张证书，
        // 指纹为 null。前端据此整块隐去身份区，而不是显示一个占位横杠——
        // 一个空着的指纹栏比没有这一栏更容易被误读成「核对过了」。
        var fingerprint = _configuration["Security:ServerCertificate:Fingerprint"];

        return OkData(new ServerIdentityResponse
        {
            Fingerprint = string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint,
            Version = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        });
    }
}

/// <summary>登录页身份区的内容。字段只增不改——它是匿名可访问的。</summary>
public sealed class ServerIdentityResponse
{
    /// <summary>服务端 TLS 证书 SHA-256 指纹（大写十六进制，无分隔符）。Secure 形态下为 null。</summary>
    public string? Fingerprint { get; set; }

    /// <summary>服务端版本号。</summary>
    public string? Version { get; set; }
}
