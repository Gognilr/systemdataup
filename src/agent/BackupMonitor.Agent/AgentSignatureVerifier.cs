using System.Security.Cryptography;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>
/// Agent 侧验证服务端签发的指令和配置。
/// 生产包只持有公钥，不持有服务端签名私钥或 HMAC 密钥。
/// </summary>
public sealed class AgentSignatureVerifier : IDisposable
{
    private readonly RSA? _rsa;
    private readonly bool _allowUnsigned;
    private readonly ILogger<AgentSignatureVerifier> _logger;

    public AgentSignatureVerifier(
        IOptions<AgentOptions> options,
        ILogger<AgentSignatureVerifier> logger)
    {
        _allowUnsigned = options.Value.AllowUnsignedCommands;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(options.Value.ServerSigningPublicKey))
            return;

        try
        {
            _rsa = RSA.Create();
            _rsa.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(options.Value.ServerSigningPublicKey),
                out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            _rsa?.Dispose();
            throw new InvalidOperationException(
                "Agent:ServerSigningPublicKey 必须是 Base64 编码的 SubjectPublicKeyInfo 公钥",
                ex);
        }
    }

    public bool VerifyCommand(CommandDto command, Guid clientId)
    {
        if (command.Id == Guid.Empty
            || clientId == Guid.Empty
            || string.IsNullOrWhiteSpace(command.Nonce)
            || command.ExpiresAt <= DateTime.UtcNow)
        {
            return false;
        }

        // 三级回退 v2 → v1 → legacy：发布顺序要求 Agent 先于服务端升级，
        // 因此新 Agent 必须能验尚未升级的服务端签出来的 v1 指令。
        // 灰度窗口结束后删掉后两级（已登记在 docs/系统审查-整改追踪.md）。
        var payload = AgentSignatureCanonicalizer.CommandPayload(
            command.Id,
            command.Nonce,
            command.Type,
            clientId,
            command.ExpiresAt,
            command.TaskId,
            command.CandidateBackupSetId,
            command.Payload);
        return Verify(payload, command.Signature)
            || Verify(
                AgentSignatureCanonicalizer.CommandPayloadV1(
                    command.Id,
                    command.Nonce,
                    command.Type,
                    clientId,
                    command.ExpiresAt),
                command.Signature)
            || Verify(
                AgentSignatureCanonicalizer.LegacyCommandPayload(
                    command.Id,
                    command.Nonce,
                    command.Type,
                    clientId,
                    command.ExpiresAt),
                command.Signature);
    }

    public bool VerifyConfig(AgentConfigResponse config, Guid clientId)
    {
        if (clientId == Guid.Empty || config.Version < 0 || string.IsNullOrWhiteSpace(config.Signature))
            return false;

        var payload = AgentSignatureCanonicalizer.ConfigPayload(
            config.Version,
            clientId,
            config.Tasks,
            config.MonitoredServices,
            config.GlobalSettings);
        return Verify(payload, config.Signature)
            || Verify(
                AgentSignatureCanonicalizer.LegacyConfigPayload(
                    config.Version,
                    clientId,
                    config.Tasks,
                    config.MonitoredServices,
                    config.GlobalSettings),
                config.Signature);
    }

    private bool Verify(byte[] payload, string? signature)
    {
        if (_rsa is null)
        {
            if (_allowUnsigned)
            {
                _logger.LogWarning(
                    "Agent 未配置服务端签名公钥，因 AllowUnsignedCommands=true 跳过签名校验");
                return true;
            }

            _logger.LogError("Agent 未配置服务端签名公钥，拒绝未验签的指令或配置");
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature)
            || !signature.StartsWith("rsa-sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var encoded = signature["rsa-sha256:".Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var raw = Convert.FromBase64String(encoded);
            return _rsa.VerifyData(payload, raw, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose() => _rsa?.Dispose();
}
