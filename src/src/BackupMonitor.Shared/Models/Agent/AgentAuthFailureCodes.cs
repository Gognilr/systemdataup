namespace BackupMonitor.Shared.Models.Agent;

/// <summary>
/// Agent 认证失败时 401 响应体里的 error.code（方案 B 的线上契约）。
///
/// 放在 Shared 而不是各写各的：服务端按这些字符串写响应，Agent 按它们决定
/// 「重新登记」还是「安静退让」，两边一旦对不上，后果不是报错而是静默地
/// 什么都不做——最坏的一种失败方式。放在同一个文件里，改的时候两边一起改。
/// </summary>
public static class AgentAuthFailureCodes
{
    /// <summary>这条连接压根没出示客户端证书。多半是连接复用了注册阶段的匿名连接，重新握手即可。</summary>
    public const string CertificateMissing = "CLIENT_CERTIFICATE_MISSING";

    /// <summary>指纹查不到记录：服务端换过空库，本机身份在服务端已不存在。可以重新登记。</summary>
    public const string IdentityUnknown = "CLIENT_IDENTITY_UNKNOWN";

    /// <summary>证书存在但已吊销/作废。身份还在，重新登记解决不了问题。</summary>
    public const string CertificateRevoked = "CLIENT_CERTIFICATE_REVOKED";

    /// <summary>证书已过期。续签路径的事，不是重新登记的事。</summary>
    public const string CertificateExpired = "CLIENT_CERTIFICATE_EXPIRED";

    /// <summary>客户端被管理员禁用或注销。客户端收到它必须安静退让，不得重新登记。</summary>
    public const string ClientDisabled = "CLIENT_DISABLED";

    /// <summary>客户端还在等审批。</summary>
    public const string ClientPendingApproval = "CLIENT_PENDING_APPROVAL";

    /// <summary>代理透传的证书头不可信或格式非法，与客户端身份无关。</summary>
    public const string ForwardedRejected = "CLIENT_FORWARDED_CERTIFICATE_REJECTED";
}
