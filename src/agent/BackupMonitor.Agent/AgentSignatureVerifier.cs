using System.Security.Cryptography;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>
/// 配置验签失败的具体原因。
///
/// 以前 VerifyConfig 只返回 bool，六种毫不相干的失败在日志里长得一模一样，
/// 都是一句「服务端配置签名校验失败」。2026-09-11 OA 服务器为此刷了 2336 条
/// 同样的告警，而排查只能靠猜。
/// </summary>
public enum ConfigVerificationFailure
{
    /// <summary>验签通过。</summary>
    None = 0,

    /// <summary>本机还没有客户端 ID——跟签名无关，是注册没完成。</summary>
    MissingClientId,

    /// <summary>配置版本号为负，响应本身就不合法。</summary>
    InvalidVersion,

    /// <summary>服务端根本没带签名字段。</summary>
    MissingSignature,

    /// <summary>本机没有配置服务端签名公钥，无从验起。</summary>
    PublicKeyNotConfigured,

    /// <summary>签名字段格式不对（前缀不符或不是合法 Base64）。</summary>
    MalformedSignature,

    /// <summary>签名验不过：公钥与服务端当前签名私钥不是一对，或内容被改过。</summary>
    SignatureMismatch
}

/// <summary>
/// Agent 侧验证服务端签发的指令和配置。
/// 生产包只持有公钥，不持有服务端签名私钥或 HMAC 密钥。
///
/// 公钥走 <see cref="IOptionsMonitor{TOptions}"/> 而不是 <see cref="IOptions{TOptions}"/>：
/// 后者在构造时快照一次，此后进程生命周期内永不重读。安装器改写了 appsettings.json
/// 里的公钥、而服务没重启时，Agent 会攥着旧公钥把每一份配置都判成伪造——
/// 2026-09-11 OA 服务器就这样静默中断了 6 小时 41 分钟，直到有人重启服务才恢复。
/// </summary>
public sealed class AgentSignatureVerifier : IDisposable
{
    private const string SignaturePrefix = "rsa-sha256:";

    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly bool _allowUnsigned;
    private readonly ILogger<AgentSignatureVerifier> _logger;

    /// <summary>保护 _rsa / _loadedKey / _fingerprint 的整体替换。</summary>
    private readonly object _gate = new();

    private RSA? _rsa;
    private string? _loadedKey;
    private string? _fingerprint;

    public AgentSignatureVerifier(
        IOptionsMonitor<AgentOptions> options,
        ILogger<AgentSignatureVerifier> logger)
    {
        _options = options;
        _allowUnsigned = options.CurrentValue.AllowUnsignedCommands;
        _logger = logger;

        var configured = options.CurrentValue.ServerSigningPublicKey;
        if (string.IsNullOrWhiteSpace(configured))
            return;

        try
        {
            _rsa = Import(configured);
            _loadedKey = configured;
            _fingerprint = FingerprintOf(configured);

            // 启动时就把指纹写进日志：出问题时「客户端认的是哪把公钥」是第一个要回答的
            // 问题，而它必须能从客户端发来的日志文件里直接读到，不能要求对方再跑一遍命令。
            _logger.LogInformation("服务端签名公钥已加载，指纹 {Fingerprint}", _fingerprint);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            _rsa?.Dispose();
            throw new InvalidOperationException(
                "Agent:ServerSigningPublicKey 必须是 Base64 编码的 SubjectPublicKeyInfo 公钥",
                ex);
        }
    }

    /// <summary>当前生效的公钥指纹（SHA256 前 8 个十六进制字符）；没配公钥时为 null。</summary>
    public string? PublicKeyFingerprint
    {
        get
        {
            CurrentKey();
            lock (_gate)
                return _fingerprint;
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

    /// <summary>验证配置签名，返回 <see cref="ConfigVerificationFailure.None"/> 表示通过。</summary>
    public ConfigVerificationFailure VerifyConfig(AgentConfigResponse config, Guid clientId)
    {
        if (clientId == Guid.Empty)
            return ConfigVerificationFailure.MissingClientId;

        if (config.Version < 0)
            return ConfigVerificationFailure.InvalidVersion;

        if (string.IsNullOrWhiteSpace(config.Signature))
            return ConfigVerificationFailure.MissingSignature;

        var rsa = CurrentKey();
        if (rsa is null)
        {
            if (_allowUnsigned)
            {
                _logger.LogWarning("Agent 未配置服务端签名公钥，因 AllowUnsignedCommands=true 跳过配置验签");
                return ConfigVerificationFailure.None;
            }

            return ConfigVerificationFailure.PublicKeyNotConfigured;
        }

        var raw = DecodeSignature(config.Signature);
        if (raw is null)
            return ConfigVerificationFailure.MalformedSignature;

        var payload = AgentSignatureCanonicalizer.ConfigPayload(
            config.Version,
            clientId,
            config.Tasks,
            config.MonitoredServices,
            config.GlobalSettings);
        if (VerifyWith(rsa, payload, raw))
            return ConfigVerificationFailure.None;

        var legacy = AgentSignatureCanonicalizer.LegacyConfigPayload(
            config.Version,
            clientId,
            config.Tasks,
            config.MonitoredServices,
            config.GlobalSettings);
        return VerifyWith(rsa, legacy, raw)
            ? ConfigVerificationFailure.None
            : ConfigVerificationFailure.SignatureMismatch;
    }

    /// <summary>失败原因的人话版本，直接进日志和告警——看的人不该去查枚举定义。</summary>
    public static string Describe(ConfigVerificationFailure failure) => failure switch
    {
        ConfigVerificationFailure.None => "验签通过",
        ConfigVerificationFailure.MissingClientId => "本机还没有客户端 ID，注册尚未完成",
        ConfigVerificationFailure.InvalidVersion => "服务端返回的配置版本号非法",
        ConfigVerificationFailure.MissingSignature => "服务端返回的配置没有带签名",
        ConfigVerificationFailure.PublicKeyNotConfigured => "本机没有配置服务端签名公钥，需要重新运行客户端安装器",
        ConfigVerificationFailure.SignatureMismatch =>
            "签名验不过：本机存的服务端公钥与服务端当前的签名私钥不是一对（服务端换过密钥或重装过），"
            + "需要在这台机器上重新运行客户端安装器",
        ConfigVerificationFailure.MalformedSignature => "签名字段格式不对",
        _ => failure.ToString()
    };

    /// <summary>
    /// 取当前公钥，配置变了就重新导入。
    ///
    /// 每次验签都比一次字符串，代价可以忽略；换来的是「改完 appsettings 不必重启服务」。
    /// </summary>
    private RSA? CurrentKey()
    {
        var configured = _options.CurrentValue.ServerSigningPublicKey ?? string.Empty;

        lock (_gate)
        {
            if (string.Equals(configured, _loadedKey, StringComparison.Ordinal))
                return _rsa;

            if (string.IsNullOrWhiteSpace(configured))
            {
                _logger.LogError("Agent:ServerSigningPublicKey 已被清空，拒绝一切未验签的指令与配置");
                _rsa?.Dispose();
                _rsa = null;
                _fingerprint = null;
                _loadedKey = configured;
                return null;
            }

            RSA rebuilt;
            try
            {
                rebuilt = Import(configured);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // 刻意不更新 _loadedKey：下一轮还会再试一次。一次手滑写坏了配置文件，
                // 不该让 Agent 永久停在「不再看这个文件」的状态上。
                _logger.LogError(ex,
                    "Agent:ServerSigningPublicKey 不是合法的 Base64 SubjectPublicKeyInfo 公钥，继续沿用上一份");
                return _rsa;
            }

            _rsa?.Dispose();
            _rsa = rebuilt;
            _loadedKey = configured;
            _fingerprint = FingerprintOf(configured);
            _logger.LogWarning("服务端签名公钥已重新加载，指纹 {Fingerprint}", _fingerprint);
            return _rsa;
        }
    }

    private static RSA Import(string base64PublicKey)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64PublicKey), out _);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>公钥指纹：SHA256 的前 8 个十六进制字符。只用于比对，不用于安全判定。</summary>
    private static string FingerprintOf(string base64PublicKey)
    {
        try
        {
            var hash = SHA256.HashData(Convert.FromBase64String(base64PublicKey));
            return Convert.ToHexString(hash)[..8];
        }
        catch (FormatException)
        {
            return "无法计算";
        }
    }

    private static byte[]? DecodeSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || !signature.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var encoded = signature[SignaturePrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool VerifyWith(RSA rsa, byte[] payload, byte[] signature)
    {
        try
        {
            return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private bool Verify(byte[] payload, string? signature)
    {
        var rsa = CurrentKey();
        if (rsa is null)
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

        var raw = DecodeSignature(signature);
        return raw is not null && VerifyWith(rsa, payload, raw);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _rsa?.Dispose();
            _rsa = null;
        }
    }
}
