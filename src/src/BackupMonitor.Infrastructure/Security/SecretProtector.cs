using Microsoft.AspNetCore.DataProtection;

namespace BackupMonitor.Infrastructure.Security;

/// <summary>
/// 密钥保护抽象（整改批次 C · C1）。
/// 用于系统内部存储的第三方凭据（SMTP 密码、企业微信/钉钉 webhook 地址等）——
/// 这些值需要在服务端可逆解密（供后台任务实际发信/调用），因此不能像用户口令那样用单向哈希，
/// 只能用对称加密静态存储。加密密钥由 ASP.NET Core DataProtection 密钥环管理，
/// 密钥环文件本身受操作系统 ACL 保护（Turnkey 模式复用 LocalServerBootstrap 已建好的
/// ProgramData 数据目录；Secure 模式读 Security:DataProtection:KeyPath）。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密明文，返回带 "enc:v1:" 前缀的密文；null/空字符串原样返回</summary>
    string? Protect(string? plaintext);

    /// <summary>
    /// 解密。若 payload 不带 "enc:v1:" 前缀（历史明文），原样返回，
    /// 供调用方判断是否需要就地重新加密（惰性迁移）。null/空字符串原样返回。
    /// </summary>
    string? Unprotect(string? payload);

    /// <summary>是否已经是本抽象加密过的密文（约定前缀 "enc:v1:"）</summary>
    bool IsProtected(string? value);
}

/// <summary>基于 Microsoft.AspNetCore.DataProtection 的实现</summary>
public class SecretProtector : ISecretProtector
{
    /// <summary>密文前缀：区分"已加密"与"历史明文"，供惰性迁移判断</summary>
    public const string Prefix = "enc:v1:";

    /// <summary>DataProtection Purpose 字符串：换掉会导致所有历史密文无法解密，不要改</summary>
    private const string Purpose = "BackupMonitor.NotificationSecrets.v1";

    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        return Prefix + _protector.Protect(plaintext);
    }

    public string? Unprotect(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return payload;

        if (!IsProtected(payload))
            return payload; // 历史明文：原样返回，由调用方决定是否重新加密落库

        var cipher = payload[Prefix.Length..];
        return _protector.Unprotect(cipher);
    }
}
