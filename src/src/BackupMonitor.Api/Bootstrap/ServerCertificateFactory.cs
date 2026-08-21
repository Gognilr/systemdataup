using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BackupMonitor.Api.Bootstrap;

/// <summary>
/// 生成和读取 LAN Turnkey 使用的服务端证书。
/// 证书只用于服务端 TLS；客户端身份仍由独立的客户端 CA 和 mTLS 证书负责。
/// </summary>
public static class ServerCertificateFactory
{
    public const int ValidityYears = 2;

    public static X509Certificate2 CreateAndWrite(
        string path,
        string password,
        string? machineName = null,
        DateTimeOffset? now = null,
        Action<string>? protectTemporaryFile = null)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("服务端证书保护口令不能为空。");

        var certificate = CreateSelfSigned(machineName ?? Environment.MachineName, now ?? DateTimeOffset.UtcNow);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("服务端证书路径没有父目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (File.Create(temporaryPath))
            {
            }
            protectTemporaryFile?.Invoke(temporaryPath);
            File.WriteAllBytes(temporaryPath, certificate.Export(X509ContentType.Pfx, password));
            File.Move(temporaryPath, path, overwrite: true);
            return certificate;
        }
        catch
        {
            TryDelete(temporaryPath);
            certificate.Dispose();
            throw;
        }
    }

    public static X509Certificate2 Load(string path, string password)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("服务端证书文件不存在。", path);

        try
        {
            var certificate = new X509Certificate2(
                path,
                password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException($"服务端证书不包含私钥：{path}");
            }

            return certificate;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"服务端证书无法加载，请检查证书文件和保护口令：{path}", ex);
        }
    }

    /// <summary>
    /// 为 Kestrel 加载服务端证书。Windows Schannel 不接受 EphemeralKeySet，
    /// 因此这里必须使用持久化密钥集；普通指纹/PEM读取仍可使用 Load 的临时密钥集。
    /// </summary>
    public static X509Certificate2 LoadForKestrel(string path, string password)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("服务端证书文件不存在。", path);

        try
        {
            // PersistKeySet is required by Windows Schannel; EphemeralKeySet is not
            // accepted for a Kestrel server certificate. The service account's
            // profile determines the persistent key store, so forcing MachineKeySet
            // would make interactive validation and non-admin service identities fail.
            var certificate = new X509Certificate2(
                path,
                password,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException($"服务端证书不包含私钥：{path}");
            }

            return certificate;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"服务端证书无法加载，请检查证书文件和保护口令：{path}", ex);
        }
    }

    public static string GetFingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

    public static X509Certificate2 CreateSelfSigned(string machineName, DateTimeOffset now)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=BackupMonitor Server"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var dnsName in GetDnsNames(machineName))
            san.AddDnsName(dnsName);
        foreach (var address in GetLocalIpv4Addresses())
            san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build(critical: false));

        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(ValidityYears));
    }

    private static IEnumerable<string> GetDnsNames(string machineName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(machineName);
        Add(Environment.MachineName);
        Add("localhost");

        try
        {
            var host = Dns.GetHostEntry(machineName);
            Add(host.HostName);
            foreach (var alias in host.Aliases)
                Add(alias);
        }
        catch (SocketException)
        {
            // DNS 不可用时仍以机器名和 IP SAN 生成证书。
        }

        var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
        if (!string.IsNullOrWhiteSpace(domain) && !machineName.Contains('.', StringComparison.Ordinal))
            Add($"{machineName}.{domain}");

        return names;

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && value.Length <= 253
                && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_'))
                names.Add(value.TrimEnd('.'));
        }
    }

    private static IEnumerable<IPAddress> GetLocalIpv4Addresses()
    {
        var addresses = new HashSet<IPAddress>();
        Add(IPAddress.Loopback);

        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (var address in network.GetIPProperties().UnicastAddresses
                             .Select(item => item.Address)
                             .Where(address => address.AddressFamily == AddressFamily.InterNetwork))
                    Add(address);
            }
        }
        catch (NetworkInformationException)
        {
            // 没有网络信息时至少保留回环地址，安装后可在维护模式重新签发。
        }

        return addresses;

        void Add(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
                addresses.Add(address);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 保留原始异常，临时文件由后续维护清理。
        }
    }
}
