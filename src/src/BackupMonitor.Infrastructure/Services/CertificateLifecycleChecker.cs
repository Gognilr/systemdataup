using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupMonitor.Core.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 证书到期巡检（整改清单 2026-09-10 · R9 第 1 点 / R10）。
///
/// 三种证书里只有客户端证书（365 天）原本就是完整闭环：到期前自动续签、
/// 续签失败会告警。另外两种既不续签也不提醒：
///
/// - **服务端 TLS 证书**（2 年）：启动时只检查文件在不在（LocalServerBootstrap），
///   过期照样加载照样用。Agent 只按指纹校验，过期了照样连得上；
///   但管理网页会开始报证书过期，而一旦点「重新签发」，指纹就变了，
///   全网 Agent 都连不上——**到期这件事必须提前两个月让人知道**，
///   因为处置它需要一个有准备的过渡期，不是当天能做完的事。
///
/// - **客户端 CA**（10 年）：客户端证书签发时会被 CA 的 NotAfter 截断
///   （CertificateAuthority.IssueClientCertificateAsync），也就是第 9 年起签出来的
///   客户端证书会越来越短，没有任何提示；到第 10 年，续签只能签出已经过期的证书，
///   全体 Agent 同时失效。180 天的提前量对应的是「重建 CA + 全网重新登记」这件事的体量。
/// </summary>
public interface ICertificateLifecycleChecker
{
    Task CheckAsync(CancellationToken ct = default);
}

public class CertificateLifecycleChecker : ICertificateLifecycleChecker
{
    /// <summary>服务端 TLS 证书剩余天数：≤ 这个数开始告警。</summary>
    public const int ServerWarnDays = 60;

    /// <summary>服务端 TLS 证书剩余天数：≤ 这个数升级为严重。</summary>
    public const int ServerCriticalDays = 14;

    /// <summary>客户端 CA 剩余天数：≤ 这个数开始告警。重建 CA 要全网重新登记，提前量必须够大。</summary>
    public const int ClientCaWarnDays = 180;

    /// <summary>客户端 CA 剩余天数：≤ 这个数升级为严重。</summary>
    public const int ClientCaCriticalDays = 60;

    private const string ServerAlertKey = "system:certificate:server_tls";
    private const string ClientCaAlertKey = "system:certificate:client_ca";

    private readonly IConfiguration _configuration;
    private readonly IAlertingService _alerting;
    private readonly ILogger<CertificateLifecycleChecker> _logger;

    public CertificateLifecycleChecker(
        IConfiguration configuration,
        IAlertingService alerting,
        ILogger<CertificateLifecycleChecker> logger)
    {
        _configuration = configuration;
        _alerting = alerting;
        _logger = logger;
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        await CheckOneAsync(
            _configuration["Security:ServerCertificate:CertPath"],
            _configuration["Security:ServerCertificate:Password"],
            ServerAlertKey,
            "server_certificate_expiring",
            "服务端 TLS 证书即将到期",
            ServerWarnDays,
            ServerCriticalDays,
            remaining =>
                $"服务端 TLS 证书还有 {remaining} 天到期。"
                + "重新签发会改变服务端指纹，全网 Agent 都按指纹固定连接——"
                + "请先在服务管理台按「预备新证书 → 等客户端就绪 → 启用」的过渡步骤走，"
                + "不要直接重新签发。",
            ct);

        await CheckOneAsync(
            _configuration["Security:ClientCa:CertPath"],
            _configuration["Security:ClientCa:Password"],
            ClientCaAlertKey,
            "client_ca_expiring",
            "客户端 CA 即将到期",
            ClientCaWarnDays,
            ClientCaCriticalDays,
            remaining =>
                $"客户端 CA 还有 {remaining} 天到期。客户端证书的有效期会被 CA 截断，"
                + "从现在起签发出来的客户端证书只会越来越短；CA 到期当天全体 Agent 同时失效。"
                + "重建 CA 需要全网重新登记，请尽早安排。",
            ct);

        await CheckClientCertificateTruncationAsync(ct);
    }

    /// <summary>
    /// 「从现在起签发的客户端证书都会被 CA 截断」（R10 第 2 点）。
    ///
    /// 判据是 CA 剩余寿命 &lt; 客户端证书的配置有效期——一旦成立，
    /// 每一张新签或续签出来的客户端证书都比该有的短，而且一年比一年短。
    /// 这是 CA 到期这件事最早的可观测信号，比 180 天那条告警还早半年。
    ///
    /// 刻意从 CA 本身算，不去挂签发那条路径：挂签发只在「正好有人来登记」时才响，
    /// 而一个稳定运行、没人加新机器的现场恰恰一年都不会触发一次——
    /// 那正是最容易一路滑到 CA 到期当天的那种现场。
    /// 签发那一侧仍然记了一条 warning 日志（CertificateAuthority），排障时能对上。
    /// </summary>
    private async Task CheckClientCertificateTruncationAsync(CancellationToken ct)
    {
        var path = _configuration["Security:ClientCa:CertPath"];
        var password = _configuration["Security:ClientCa:Password"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        DateTime caNotAfter;
        try
        {
            using var ca = new X509Certificate2(path, password, X509KeyStorageFlags.EphemeralKeySet);
            caNotAfter = ca.NotAfter.ToUniversalTime();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return;   // 上面那条检查已经记过日志，这里不重复刷。
        }

        var clientValidityDays = _configuration.GetValue("Security:ClientCa:ClientCertValidityDays", 365);
        var caRemainingDays = (int)Math.Floor((caNotAfter - DateTime.UtcNow).TotalDays);

        if (caRemainingDays >= clientValidityDays)
        {
            await _alerting.RecoverAsync(TruncationAlertKey, ct);
            return;
        }

        await _alerting.RaiseAsync(
            TruncationAlertKey,
            // 已经短到不足 30 天，等于「续签出来的证书刚签就快过期」，那是严重。
            caRemainingDays <= 30 ? AlertLevel.Critical : AlertLevel.Warning,
            "client_certificate_truncated",
            "新签发的客户端证书正在被 CA 截断",
            $"客户端 CA 只剩 {caRemainingDays} 天，而客户端证书的有效期是 {clientValidityDays} 天。"
            + "从现在起每一张新签或续签的客户端证书都会被截短到 CA 的到期日，而且一次比一次短；"
            + $"到 {caNotAfter:yyyy-MM-dd} 全体 Agent 会同时失效。重建 CA 需要全网重新登记，请立即安排。",
            ct: ct);
    }

    private const string TruncationAlertKey = "system:certificate:client_cert_truncated";

    private async Task CheckOneAsync(
        string? path,
        string? password,
        string alertKey,
        string category,
        string title,
        int warnDays,
        int criticalDays,
        Func<int, string> message,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;   // Secure 形态下 TLS 由 nginx 终结，本进程手里没有这张证书，不适用。

        DateTime notAfter;
        try
        {
            // EphemeralKeySet：只读 NotAfter，不该在机器的密钥容器里留下痕迹。
            using var certificate = new X509Certificate2(path, password, X509KeyStorageFlags.EphemeralKeySet);
            notAfter = certificate.NotAfter.ToUniversalTime();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // 读不出来不报「即将到期」——那会是一条内容错误的告警。
            // 但也不能一声不响：读不了证书本身就是一件要人看的事。
            _logger.LogWarning(ex, "无法读取证书以检查有效期：{Category}", category);
            return;
        }

        var remaining = (int)Math.Floor((notAfter - DateTime.UtcNow).TotalDays);
        if (remaining > warnDays)
        {
            await _alerting.RecoverAsync(alertKey, ct);
            return;
        }

        // 已经过期时 remaining 是负数，正文要说「已经过期」而不是「还有 -3 天」。
        var body = remaining < 0
            ? $"证书已于 {notAfter:yyyy-MM-dd} 过期（{-remaining} 天前）。" + message(remaining)
            : message(remaining);

        await _alerting.RaiseAsync(
            alertKey,
            remaining <= criticalDays ? AlertLevel.Critical : AlertLevel.Warning,
            category,
            title,
            body,
            ct: ct);
    }
}
