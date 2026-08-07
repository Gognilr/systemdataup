using System.Security.Cryptography;
using System.Text;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Infrastructure.Common;
using Microsoft.Extensions.Configuration;

namespace BackupMonitor.Infrastructure.Security;

/// <summary>
/// 指令与配置签名服务（设计书 23.3 防重放 / 12.1 指令签名 / 11.2 配置签名）。
/// 使用 HMAC-SHA256，密钥来自配置 Security:CommandSigningKey。
/// </summary>
public class CommandSigner
{
    private readonly byte[] _key;

    public CommandSigner(IConfiguration configuration)
    {
        var key = configuration["Security:CommandSigningKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("缺少配置 Security:CommandSigningKey（指令签名密钥）");
        _key = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>对指令签名：覆盖 id、nonce、类型、客户端、过期时间，防篡改防重放</summary>
    public string SignCommand(Command command)
    {
        var payload = string.Join("|",
            command.Id.ToString(),
            command.Nonce,
            EnumMapping.ToSnakeCase(command.CommandType),
            command.ClientId.ToString(),
            command.ExpiresAt.ToString("O"));
        return ComputeHmac(payload);
    }

    /// <summary>对下发配置内容签名</summary>
    public string SignConfig(long version, Guid clientId, string configJson)
    {
        var payload = string.Join("|", version, clientId.ToString(), configJson);
        return ComputeHmac(payload);
    }

    private string ComputeHmac(string payload)
    {
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
