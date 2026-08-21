using System.Security.Cryptography;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Configuration;

namespace BackupMonitor.Infrastructure.Security;

/// <summary>
/// 指令与配置签名服务（设计书 23.3 防重放 / 12.1 指令签名 / 11.2 配置签名）。
/// 使用服务端 RSA 私钥签名。私钥来自配置 Security:CommandSigningPrivateKey，
/// 仅在兼容旧部署且没有 RSA 私钥时回退到旧 HMAC 配置；生产 Agent 不接受该回退签名。
/// </summary>
public class CommandSigner
{
    private readonly RSA? _rsa;
    private readonly byte[]? _legacyHmacKey;

    public CommandSigner(IConfiguration configuration)
    {
        var privateKey = configuration["Security:CommandSigningPrivateKey"];
        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            try
            {
                _rsa = RSA.Create();
                _rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
                return;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                _rsa?.Dispose();
                throw new InvalidOperationException(
                    "Security:CommandSigningPrivateKey 必须是 Base64 编码的 PKCS#8 RSA 私钥",
                    ex);
            }
        }

        var legacyKey = configuration["Security:CommandSigningKey"];
        var allowLegacy = configuration.GetValue("Security:AllowLegacyCommandHmac", false);
        if (!allowLegacy || string.IsNullOrWhiteSpace(legacyKey))
            throw new InvalidOperationException(
                "缺少配置 Security:CommandSigningPrivateKey；仅在显式设置 Security:AllowLegacyCommandHmac=true 时允许旧 HMAC 迁移");
        _legacyHmacKey = System.Text.Encoding.UTF8.GetBytes(legacyKey);
    }

    /// <summary>对指令签名：覆盖 id、nonce、类型、客户端、过期时间，防篡改防重放</summary>
    public string SignCommand(Command command)
    {
        var payload = AgentSignatureCanonicalizer.CommandPayload(
            command.Id,
            command.Nonce,
            EnumMapping.ToSnakeCase(command.CommandType),
            command.ClientId,
            command.ExpiresAt);
        return Sign(payload);
    }

    /// <summary>对下发配置内容签名</summary>
    public string SignConfig(AgentConfigResponse response, Guid clientId)
    {
        var payload = AgentSignatureCanonicalizer.ConfigPayload(
            response.Version,
            clientId,
            response.Tasks,
            response.MonitoredServices,
            response.GlobalSettings);
        return Sign(payload);
    }

    /// <summary>
    /// 导出仅用于 Agent 安装引导的 RSA 公钥。
    /// 公钥本身可公开，服务端私钥始终只保留在服务端配置中。
    /// </summary>
    public string ExportPublicKey()
    {
        if (_rsa is null)
            throw new InvalidOperationException("当前服务端未配置 RSA 指令签名私钥，无法生成 Agent 安装引导信息");

        return Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());
    }

    private string Sign(byte[] payload)
    {
        if (_rsa is not null)
        {
            var signature = _rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return "rsa-sha256:" + ToBase64Url(signature);
        }

        using var hmac = new HMACSHA256(_legacyHmacKey!);
        return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
