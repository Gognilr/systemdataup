using BackupMonitor.Shared.Security;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 指纹的规范化与展示格式是「带外核对」这套机制的地基：
/// 服务端安装器显示一次、客户端安装器显示一次，由人比对两边是否一致。
/// 两端共用这一份实现，所以它的行为必须钉死——格式一旦两边不一致，
/// 人眼比对就会变成负担，进而诱发「看着差不多就点确认」。
/// </summary>
public sealed class CertificateFingerprintTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD", "AABBCCDD")]
    [InlineData("aa bb cc dd", "AABBCCDD")]
    [InlineData("aa-bb-cc-dd", "AABBCCDD")]
    [InlineData("  AaBbCcDd  ", "AABBCCDD")]
    public void 规范化去掉一切分隔符并转大写(string input, string expected)
    {
        Assert.Equal(expected, CertificateFingerprint.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::::")]
    public void 空值规范化为空串(string? input)
    {
        Assert.Equal(string.Empty, CertificateFingerprint.Normalize(input));
    }

    /// <summary>
    /// 比较必须在规范化之后做。工单里抄来的指纹常常带冒号，
    /// 而实际算出来的不带——按字面量比会把一致的判成不一致，
    /// 操作员多试两次就会开始怀疑这套核对本身，转而随手点确认。
    /// </summary>
    [Fact]
    public void 比较忽略分隔符与大小写()
    {
        Assert.True(CertificateFingerprint.Matches("AA:BB:CC:DD", "aabbccdd"));
        Assert.True(CertificateFingerprint.Matches("aa bb cc dd", "AA-BB-CC-DD"));
        Assert.False(CertificateFingerprint.Matches("AABBCCDD", "AABBCCDE"));
    }

    /// <summary>空指纹不能被判成「匹配」——否则缺失取值会被当作核对通过。</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "AABBCCDD")]
    [InlineData("AABBCCDD", null)]
    public void 空指纹一律不匹配(string? left, string? right)
    {
        Assert.False(CertificateFingerprint.Matches(left, right));
    }

    [Fact]
    public void SHA256指纹排成四行每行四组()
    {
        var fingerprint = string.Concat(Enumerable.Repeat("0123456789ABCDEF", 4));
        Assert.Equal(64, fingerprint.Length);

        var lines = CertificateFingerprint
            .ToDisplayBlock(fingerprint)
            .Split(Environment.NewLine);

        Assert.Equal(4, lines.Length);
        foreach (var line in lines)
        {
            var groups = line.Split("  ");
            Assert.Equal(4, groups.Length);
            Assert.All(groups, group => Assert.Equal(4, group.Length));
        }
    }

    /// <summary>展示格式只加分隔，不能改动内容——去掉分隔后必须还原成原值。</summary>
    [Fact]
    public void 展示格式不改变指纹内容()
    {
        var fingerprint = string.Concat(Enumerable.Repeat("0123456789ABCDEF", 4));
        var displayed = CertificateFingerprint.ToDisplayBlock(fingerprint);

        Assert.Equal(fingerprint, CertificateFingerprint.Normalize(displayed));
        Assert.True(CertificateFingerprint.Matches(fingerprint, displayed));
    }

    /// <summary>长度不是 4 的整数倍时不能丢字符（异常证书或截断输入）。</summary>
    [Fact]
    public void 非整组长度不丢字符()
    {
        const string odd = "AABBCCDDE";
        Assert.Equal(odd, CertificateFingerprint.Normalize(CertificateFingerprint.ToDisplayBlock(odd)));
    }
}
