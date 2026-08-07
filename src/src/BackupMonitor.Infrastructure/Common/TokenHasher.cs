using System.Security.Cryptography;
using System.Text;

namespace BackupMonitor.Infrastructure.Common;

/// <summary>令牌/敏感凭据哈希工具（只存哈希，明文不入库）</summary>
public static class TokenHasher
{
    /// <summary>SHA-256 十六进制小写</summary>
    public static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>生成加密安全的随机令牌（base64url）</summary>
    public static string GenerateToken(int byteLength = 48)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>归一化十六进制字符串（小写去空白）</summary>
    public static string NormalizeHex(string value) => value.Trim().ToLowerInvariant();
}
