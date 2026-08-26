using BackupMonitor.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 整改批次 C · C1：SecretProtector 是 SMTP 密码 / 企业微信/钉钉 webhook 加密存储的唯一入口，
/// 也是历史明文惰性迁移判断（IsProtected）依赖的地基——这里的行为一旦错了，
/// 要么密文解不出来（服务发不出通知），要么明文永远判定为"已加密"（迁移永远不触发）。
/// 用 UseEphemeralDataProtectionProvider 避免测试依赖文件系统密钥环。
/// </summary>
public sealed class SecretProtectorTests
{
    private static ISecretProtector CreateProtector()
    {
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        return new SecretProtector(provider);
    }

    [Fact]
    public void 加密解密往返得到原文()
    {
        var protector = CreateProtector();
        const string plaintext = "S3cr3t!Pa55w0rd";

        var cipher = protector.Protect(plaintext);
        var decrypted = protector.Unprotect(cipher);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void 密文带有约定前缀且明文不含在密文里()
    {
        var protector = CreateProtector();
        const string plaintext = "wecom-webhook-access-token-xyz";

        var cipher = protector.Protect(plaintext)!;

        Assert.StartsWith(SecretProtector.Prefix, cipher, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, cipher, StringComparison.Ordinal);
        Assert.True(protector.IsProtected(cipher));
    }

    /// <summary>
    /// 惰性迁移的前提：历史明文（不带 enc:v1: 前缀）Unprotect 时原样返回，而不是抛异常或返回 null，
    /// 否则升级后首次读取已有配置就会直接炸掉或把密码丢失。
    /// </summary>
    [Fact]
    public void 未加密的历史明文Unprotect时原样返回()
    {
        var protector = CreateProtector();
        const string legacyPlaintext = "legacy-plain-password";

        Assert.False(protector.IsProtected(legacyPlaintext));
        Assert.Equal(legacyPlaintext, protector.Unprotect(legacyPlaintext));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空值原样透传(string? value)
    {
        var protector = CreateProtector();

        Assert.Equal(value, protector.Protect(value));
        Assert.Equal(value, protector.Unprotect(value));
        Assert.False(protector.IsProtected(value));
    }

    [Fact]
    public void 两次加密同一明文产生不同密文()
    {
        // DataProtection 的密文带随机 IV/nonce；相同明文两次加密结果不应相同，
        // 否则等于把"密文是否相等"当成了"明文是否相等"的旁路信道。
        var protector = CreateProtector();
        const string plaintext = "same-secret";

        var cipher1 = protector.Protect(plaintext);
        var cipher2 = protector.Protect(plaintext);

        Assert.NotEqual(cipher1, cipher2);
        Assert.Equal(plaintext, protector.Unprotect(cipher1));
        Assert.Equal(plaintext, protector.Unprotect(cipher2));
    }
}
