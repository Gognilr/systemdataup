using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupMonitor.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Security;

/// <summary>签发客户端证书的结果</summary>
public record IssuedCertificate(string CertificatePem, string Thumbprint, DateTime IssuedAt, DateTime ExpiresAt, string SerialNumber);

/// <summary>
/// 客户端证书签发服务（设计书 10.2 证书签发结果 / 23.1 客户端认证）。
/// CA 证书来自配置 Security:ClientCa:CertPath（PFX，含私钥）；
/// 未配置时自动生成仅用于开发环境的本地 CA（启动日志告警）。
/// </summary>
public class CertificateAuthority
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CertificateAuthority> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private X509Certificate2? _caCertificate;

    public CertificateAuthority(IConfiguration configuration, ILogger<CertificateAuthority> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>使用客户端注册时提交的公钥（Base64 SPKI）签发客户端证书</summary>
    public async Task<IssuedCertificate> IssueClientCertificateAsync(
        Guid clientId, string hostname, string publicKeyBase64)
    {
        var ca = await GetOrLoadCaAsync();

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            throw new BusinessException("INVALID_REQUEST", "客户端公钥不是合法的 Base64 编码", 400);
        }

        // 客户端证书的有效期必须落在 CA 有效期内。原先固定回拨一天，
        // 在本地 CA 刚生成时会早于 CA.NotBefore，导致 .NET 拒绝签发。
        var now = DateTimeOffset.UtcNow;
        var issuerNotBefore = new DateTimeOffset(ca.NotBefore.ToUniversalTime());
        var issuerNotAfter = new DateTimeOffset(ca.NotAfter.ToUniversalTime());
        var notBefore = issuerNotBefore > now.AddMinutes(-5) ? issuerNotBefore : now.AddMinutes(-5);
        var validityDays = _configuration.GetValue("Security:ClientCa:ClientCertValidityDays", 365);
        var notAfter = now.AddDays(validityDays);
        if (notAfter > issuerNotAfter)
            notAfter = issuerNotAfter;
        if (notAfter <= notBefore)
            throw new BusinessException("SERVICE_UNAVAILABLE", "客户端证书 CA 的有效期不足，无法签发客户端证书", 503);
        var serial = CreateRandomSerial();
        var dn = new X500DistinguishedName($"CN={clientId}, O=BackupMonitor, OU={SanitizeDn(hostname)}");

        X509Certificate2 signed;
        try
        {
            signed = SignWithPublicKey(dn, spki, ca, notBefore, notAfter, serial);
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BusinessException("INVALID_REQUEST", $"客户端公钥解析失败：{ex.Message}", 400);
        }

        var pem = signed.ExportCertificatePem();
        return new IssuedCertificate(
            pem,
            signed.Thumbprint.ToLowerInvariant(),
            notBefore.UtcDateTime,
            notAfter.UtcDateTime,
            Convert.ToHexString(serial).ToLowerInvariant());
    }

    /// <summary>校验公钥可解析（注册提交时提前发现错误）</summary>
    public bool CanParsePublicKey(string publicKeyBase64)
    {
        try
        {
            var spki = Convert.FromBase64String(publicKeyBase64);
            using var key = ImportPublicKey(spki);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static X509Certificate2 SignWithPublicKey(
        X500DistinguishedName dn, byte[] spki, X509Certificate2 ca,
        DateTimeOffset notBefore, DateTimeOffset notAfter, byte[] serial)
    {
        using var key = ImportPublicKey(spki)
            ?? throw new BusinessException("INVALID_REQUEST", "无法识别的客户端公钥格式（支持 RSA/ECDSA SPKI）", 400);

        X509Certificate2 signed = key switch
        {
            RSA rsa => BuildRequest(dn, rsa).Create(ca, notBefore, notAfter, serial),
            ECDsa ecdsa => BuildRequest(dn, ecdsa).Create(ca, notBefore, notAfter, serial),
            _ => throw new BusinessException("INVALID_REQUEST", "不支持的客户端公钥类型", 400)
        };

        return signed;
    }

    private static CertificateRequest BuildRequest(X500DistinguishedName dn, RSA rsa)
    {
        var req = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddClientCertExtensions(req);
        return req;
    }

    private static CertificateRequest BuildRequest(X500DistinguishedName dn, ECDsa ecdsa)
    {
        var req = new CertificateRequest(dn, ecdsa, HashAlgorithmName.SHA256);
        AddClientCertExtensions(req);
        return req;
    }

    private static void AddClientCertExtensions(CertificateRequest req)
    {
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false)); // clientAuth
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
    }

    private static AsymmetricAlgorithm? ImportPublicKey(byte[] spki)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            // 继续尝试 ECDSA
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            return null;
        }
    }

    private static byte[] CreateRandomSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // 保证正数
        return serial;
    }

    private static string SanitizeDn(string value)
    {
        var cleaned = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    private async Task<X509Certificate2> GetOrLoadCaAsync()
    {
        if (_caCertificate is not null)
            return _caCertificate;

        await _gate.WaitAsync();
        try
        {
            if (_caCertificate is not null)
                return _caCertificate;

            var configuredPath = _configuration["Security:ClientCa:CertPath"];
            var password = _configuration["Security:ClientCa:Password"];
            var isDevCa = string.IsNullOrWhiteSpace(configuredPath);

            var certPath = isDevCa
                ? Path.Combine(AppContext.BaseDirectory, "data", "dev-ca.pfx")
                : configuredPath!;

            if (isDevCa)
                _logger.LogWarning("未配置 Security:ClientCa:CertPath，使用开发环境本地 CA：{Path}", certPath);

            if (!File.Exists(certPath))
            {
                if (!isDevCa)
                {
                    throw new BusinessException(
                        "SERVICE_UNAVAILABLE",
                        "客户端证书 CA 未配置（找不到 PFX 文件），无法签发客户端证书",
                        503);
                }

                // 自动生成的开发 CA 用配置口令保护。若未配置口令则以空口令导出，
                // 否则进程重启后无从得知随机口令，PFX 将永远无法再加载。
                password ??= string.Empty;
                await GenerateDevCaAsync(certPath, password);
            }
            else if (isDevCa && password is null)
            {
                // 兼容旧版本用「随机且仅存在于内存的口令」生成的 PFX：
                // 这类文件重启后必然无法加载，明确报错并给出处置方式。
                password = string.Empty;
            }

            X509Certificate2 loaded;
            try
            {
                loaded = new X509Certificate2(certPath, password, X509KeyStorageFlags.Exportable);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "加载 CA 证书失败 {Path}", certPath);
                throw new BusinessException(
                    "SERVICE_UNAVAILABLE",
                    isDevCa
                        ? $"开发 CA {certPath} 无法加载（口令不匹配）。若为旧版本生成的文件，删除后重启将以 Security:ClientCa:Password 重新生成；注意已签发的客户端证书会随之全部失效，需重新审批。"
                        : "CA 证书无法加载，请检查 Security:ClientCa:CertPath 与 Security:ClientCa:Password",
                    503);
            }

            if (!loaded.HasPrivateKey)
            {
                loaded.Dispose();
                throw new BusinessException("SERVICE_UNAVAILABLE", "CA 证书缺少私钥，无法签发客户端证书", 503);
            }

            _caCertificate = loaded;
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>生成仅用于开发环境的自签名 CA，使用调用方给定的口令保护 PFX</summary>
    private async Task GenerateDevCaAsync(string certPath, string password)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=BackupMonitor Dev CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        var pfx = ca.Export(X509ContentType.Pfx, password);
        await File.WriteAllBytesAsync(certPath, pfx);

        _logger.LogWarning(
            "已生成开发环境 CA 证书 {Path}（口令取自 Security:ClientCa:Password{Note}）。生产环境必须替换为正式 CA。",
            certPath,
            password.Length == 0 ? "，当前为空口令" : string.Empty);
    }
}
