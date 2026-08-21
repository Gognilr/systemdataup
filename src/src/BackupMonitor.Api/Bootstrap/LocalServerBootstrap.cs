using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Configuration;

namespace BackupMonitor.Api.Bootstrap;

/// <summary>
/// 本地 Turnkey 服务端引导。
/// 
/// 普通开发/高级 Secure 部署在没有启用 BACKUPMONITOR_TURNKEY 或
/// DeploymentMode=LanSimple 时保持原有外部配置行为；服务端安装器启用后，
/// 这里负责以“显式配置/环境变量 &gt; 已持久化密钥 &gt; 首次生成”的顺序提供内部配置。
/// 私密值只写入 ProgramData 下的 server-secrets.json，不进入日志、响应或客户端包。
/// </summary>
public static class LocalServerBootstrap
{
    private static readonly ConcurrentDictionary<string, object> InProcessLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static LocalServerBootstrapResult Initialize(
        IConfiguration configuration,
        LocalServerBootstrapOptions? options = null)
    {
        options ??= new LocalServerBootstrapOptions();

        var enabled = options.Enabled
            ?? (IsTrue(Environment.GetEnvironmentVariable("BACKUPMONITOR_TURNKEY"))
                || string.Equals(configuration["DeploymentMode"], "LanSimple", StringComparison.OrdinalIgnoreCase));

        var dataDirectory = ResolveDataDirectory(configuration, options);
        var secretsPath = options.SecretsPath
            ?? Environment.GetEnvironmentVariable("BACKUPMONITOR_SERVER_SECRETS_PATH")
            ?? Path.Combine(dataDirectory, "server-secrets.json");
        secretsPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(secretsPath));

        var persistedExists = File.Exists(secretsPath);
        if (!enabled && !persistedExists)
        {
            // Secure/开发部署仍由 appsettings、user-secrets 或环境变量负责。
            // 不在普通开发进程中创建 ProgramData 文件或生成新 CA。
            return LocalServerBootstrapResult.Disabled(dataDirectory, secretsPath);
        }

        CreateDirectory(dataDirectory, options.EnforceAcl);
        CreateDirectory(Path.Combine(dataDirectory, "Repository"), options.EnforceAcl);
        CreateDirectory(Path.Combine(dataDirectory, "Staging"), options.EnforceAcl);

        var gate = InProcessLocks.GetOrAdd(secretsPath, static _ => new object());
        lock (gate)
        {
            using var fileLock = AcquireCrossProcessLock(secretsPath + ".lock");
            var persisted = ReadSecrets(secretsPath);
            var firstCreation = persisted is null;
            var effective = BuildEffectiveSecrets(configuration, persisted, dataDirectory, options);
            if (firstCreation || !SecretsEqual(persisted!, effective))
                WriteSecretsAtomically(secretsPath, effective, options.EnforceAcl);

            var caPath = ResolveCaPath(configuration, effective, dataDirectory);
            if (!File.Exists(caPath))
            {
                if (!firstCreation && !options.AllowCaRegeneration)
                    throw new InvalidOperationException(
                        $"服务端 CA 文件不存在：{caPath}。已有 server-secrets.json 时拒绝静默生成新 CA，以免现有客户端证书失效。");

                GenerateClientCa(caPath, effective.ClientCaPassword, options.EnforceAcl);
            }
            else
            {
                try
                {
                    using var ca = new X509Certificate2(
                        caPath,
                        effective.ClientCaPassword,
                        X509KeyStorageFlags.EphemeralKeySet);
                    if (!ca.HasPrivateKey)
                        throw new CryptographicException("CA 文件不包含私钥");
                }
                catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        $"服务端 CA 与 server-secrets.json 不匹配：{caPath}。请从同一份备份恢复 server-secrets.json 和 client-ca.pfx；若确认必须重建 CA，请在维护模式显式执行并重新登记全部 Agent。",
                        ex);
                }
            }

            var serverCertificatePath = ResolveServerCertificatePath(configuration, dataDirectory);
            if (!File.Exists(serverCertificatePath))
            {
                using var generated = ServerCertificateFactory.CreateAndWrite(
                    serverCertificatePath,
                    effective.ServerCertificatePassword,
                    protectTemporaryFile: options.EnforceAcl
                        ? path => SecureFileSystem.ApplyFileAcl(path)
                        : null);
            }
            else
            {
                using var existing = ServerCertificateFactory.Load(
                    serverCertificatePath,
                    effective.ServerCertificatePassword);
            }

            if (options.EnforceAcl)
            {
                ApplySecretAcl(secretsPath);
                if (File.Exists(caPath))
                    ApplySecretAcl(caPath);
                if (File.Exists(serverCertificatePath))
                    ApplySecretAcl(serverCertificatePath);
            }

            var values = BuildConfigurationValues(
                configuration,
                effective,
                dataDirectory,
                caPath,
                serverCertificatePath,
                options);
            return new LocalServerBootstrapResult(
                true,
                dataDirectory,
                secretsPath,
                values,
                effective.InstanceId);
        }
    }

    private static LocalServerSecrets BuildEffectiveSecrets(
        IConfiguration configuration,
        LocalServerSecrets? persisted,
        string dataDirectory,
        LocalServerBootstrapOptions options)
    {
        var explicitJwt = FirstNonEmpty(
            Environment.GetEnvironmentVariable("Security__Jwt__SigningKey"),
            configuration["Security:Jwt:SigningKey"]);
        var explicitCommand = FirstNonEmpty(
            Environment.GetEnvironmentVariable("Security__CommandSigningPrivateKey"),
            configuration["Security:CommandSigningPrivateKey"]);
        var explicitCaPassword = FirstNonEmpty(
            Environment.GetEnvironmentVariable("Security__ClientCa__Password"),
            configuration["Security:ClientCa:Password"]);
        var explicitServerCertificatePassword = FirstNonEmpty(
            Environment.GetEnvironmentVariable("Security__ServerCertificate__Password"),
            configuration["Security:ServerCertificate:Password"]);
        var explicitConnection = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ConnectionStrings__Default"),
            configuration.GetConnectionString("Default"));
        var explicitPostgresPassword = Environment.GetEnvironmentVariable("BACKUPMONITOR_POSTGRES_PASSWORD");
        var explicitPostgresSuperuserPassword = Environment.GetEnvironmentVariable("BACKUPMONITOR_POSTGRES_SUPERUSER_PASSWORD");
        var explicitInstanceId = FirstNonEmpty(
            Environment.GetEnvironmentVariable("BACKUPMONITOR_SERVER_INSTANCE_ID"),
            configuration["Server:InstanceId"]);
        var explicitDisplayName = FirstNonEmpty(
            Environment.GetEnvironmentVariable("BACKUPMONITOR_SERVER_DISPLAY_NAME"),
            configuration["LanMode:DisplayName"]);
        var createdAt = persisted?.CreatedAtUtc ?? DateTime.UtcNow;
        var deploymentMode = FirstNonEmpty(
            Environment.GetEnvironmentVariable("BACKUPMONITOR_DEPLOYMENT_MODE"),
            configuration["DeploymentMode"])
            ?? persisted?.DeploymentMode
            ?? "LanSimple";
        var automaticEnrollment = ParseBool(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("LanMode__AutomaticEnrollment"),
                configuration["LanMode:AutomaticEnrollment"]))
            ?? persisted?.AutomaticEnrollment
            ?? true;
        var privateNetworkOnly = ParseBool(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("LanMode__PrivateNetworkOnly"),
                configuration["LanMode:PrivateNetworkOnly"]))
            ?? persisted?.PrivateNetworkOnly
            ?? true;
        var enrollmentOpenUntil = ParseUtc(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("LanMode__EnrollmentOpenUntil"),
                configuration["LanMode:EnrollmentOpenUntil"]))
            ?? persisted?.EnrollmentOpenUntilUtc
            ?? createdAt.AddHours(24);

        var postgresPort = ParsePort(
            Environment.GetEnvironmentVariable("BACKUPMONITOR_POSTGRES_PORT")
            ?? configuration["Postgres:Port"]
            ?? persisted?.PostgresPort.ToString(),
            55432);

        return new LocalServerSecrets
        {
            SchemaVersion = 2,
            InstanceId = explicitInstanceId ?? persisted?.InstanceId ?? Guid.NewGuid().ToString("D"),
            DisplayName = explicitDisplayName ?? persisted?.DisplayName ?? Environment.MachineName,
            DeploymentMode = deploymentMode,
            AutomaticEnrollment = automaticEnrollment,
            PrivateNetworkOnly = privateNetworkOnly,
            EnrollmentOpenUntilUtc = enrollmentOpenUntil,
            JwtSigningKey = explicitJwt ?? persisted?.JwtSigningKey ?? NewSecret(48),
            CommandSigningPrivateKey = explicitCommand ?? persisted?.CommandSigningPrivateKey ?? CreateRsaPrivateKey(),
            ClientCaPassword = explicitCaPassword ?? persisted?.ClientCaPassword ?? NewSecret(32),
            ServerCertificatePassword = explicitServerCertificatePassword
                ?? FirstNonEmpty(persisted?.ServerCertificatePassword)
                ?? NewSecret(32),
            PostgresPassword = explicitPostgresPassword
                ?? (persisted is not null && persisted.SchemaVersion >= 2
                    ? persisted.PostgresPassword
                    : NewSecret(32)),
            PostgresSuperuserPassword = explicitPostgresSuperuserPassword
                ?? FirstNonEmpty(persisted?.PostgresSuperuserPassword, persisted?.PostgresPassword)
                ?? NewSecret(32),
            PostgresPort = postgresPort,
            ExplicitConnectionString = explicitConnection ?? persisted?.ExplicitConnectionString,
            CreatedAtUtc = createdAt
        };
    }

    private static Dictionary<string, string?> BuildConfigurationValues(
        IConfiguration configuration,
        LocalServerSecrets secrets,
        string dataDirectory,
        string caPath,
        string serverCertificatePath,
        LocalServerBootstrapOptions options)
    {
        var defaultConnection = secrets.ExplicitConnectionString
            ?? $"Host=127.0.0.1;Port={secrets.PostgresPort};Database=backup_monitor;Username=backup_monitor_app;Password={secrets.PostgresPassword};Pooling=true;Include Error Detail=false";

        var advertisedUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("BACKUPMONITOR_SERVER_ADVERTISED_URL"),
            configuration["LanMode:AdvertisedUrl"])
            ?? $"https://{Environment.MachineName}:5080";
        if (Uri.TryCreate(advertisedUrl, UriKind.Absolute, out var advertisedUri)
            && advertisedUri.Scheme == Uri.UriSchemeHttp)
            advertisedUrl = new UriBuilder(advertisedUri) { Scheme = Uri.UriSchemeHttps }.Uri.ToString().TrimEnd('/');

        using var serverCertificate = ServerCertificateFactory.Load(
            serverCertificatePath,
            secrets.ServerCertificatePassword);
        string repositoryPath = configuration["Storage:RepositoryPath"]
            ?? Path.Combine(dataDirectory, "Repository");
        string stagingPath = configuration["Storage:StagingPath"]
            ?? Path.Combine(dataDirectory, "Staging");

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeploymentMode"] = secrets.DeploymentMode,
            ["ConnectionStrings:Default"] = defaultConnection,
            ["Postgres:Password"] = secrets.PostgresPassword,
            ["Postgres:Port"] = secrets.PostgresPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Security:Jwt:SigningKey"] = secrets.JwtSigningKey,
            ["Security:CommandSigningPrivateKey"] = secrets.CommandSigningPrivateKey,
            // 这两个是安全下限，不跟随部署模式开关，避免旧协议或开发头被重新打开。
            ["Security:AllowLegacyCommandHmac"] = "false",
            ["Security:ClientCa:CertPath"] = caPath,
            ["Security:ClientCa:Password"] = secrets.ClientCaPassword,
            ["Security:ServerCertificate:CertPath"] = serverCertificatePath,
            ["Security:ServerCertificate:Password"] = secrets.ServerCertificatePassword,
            ["Security:ServerCertificate:Fingerprint"] = ServerCertificateFactory.GetFingerprint(serverCertificate),
            ["Security:ServerCertificate:CertificatePem"] = serverCertificate.ExportCertificatePem(),
            // 客户端认证必须来自已签发证书；开发头仅允许在显式开发配置中使用，生产启动强制关闭。
            ["Security:ClientAuth:AllowDevelopmentHeader"] = "false",
            ["Server:DataDirectory"] = dataDirectory,
            ["Server:InstanceId"] = secrets.InstanceId,
            ["Server:DisplayName"] = secrets.DisplayName,
            ["LanMode:AutomaticEnrollment"] = secrets.AutomaticEnrollment.GetValueOrDefault().ToString().ToLowerInvariant(),
            ["LanMode:PrivateNetworkOnly"] = secrets.PrivateNetworkOnly.GetValueOrDefault().ToString().ToLowerInvariant(),
            ["LanMode:EnrollmentOpenUntil"] = secrets.EnrollmentOpenUntilUtc?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["LanMode:DiscoveryEnabled"] = options.DiscoveryEnabled.ToString(),
            ["LanMode:DiscoveryPort"] = options.DiscoveryPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["LanMode:AdvertisedUrl"] = advertisedUrl,
            ["Storage:RepositoryPath"] = repositoryPath,
            ["Storage:StagingPath"] = stagingPath
        };
        // 超级用户口令只在安装器的单次初始化调用中通过显式选项返回，API 启动不进入配置。
        if (options.IncludeInstallationSecrets)
            values["Postgres:SuperuserPassword"] = secrets.PostgresSuperuserPassword;
        return values;
    }

    private static string ResolveDataDirectory(IConfiguration configuration, LocalServerBootstrapOptions options)
    {
        var value = options.DataDirectory
            ?? Environment.GetEnvironmentVariable("BACKUPMONITOR_SERVER_DATA_DIR")
            ?? configuration["Server:DataDirectory"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMonitor", "Server");
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }

    private static string ResolveCaPath(IConfiguration configuration, LocalServerSecrets secrets, string dataDirectory) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("Security__ClientCa__CertPath"),
                configuration["Security:ClientCa:CertPath"],
                Path.Combine(dataDirectory, "client-ca.pfx"))!));

    private static string ResolveServerCertificatePath(IConfiguration configuration, string dataDirectory) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("Security__ServerCertificate__CertPath"),
                configuration["Security:ServerCertificate:CertPath"],
                Path.Combine(dataDirectory, "server-certificate.pfx"))!));

    /// <summary>维护模式重新签发服务端证书；旧证书会被原子替换。</summary>
    public static string ReissueServerCertificate(
        IConfiguration configuration,
        LocalServerBootstrapOptions options)
    {
        var bootstrap = Initialize(configuration, options);
        var path = bootstrap.Values["Security:ServerCertificate:CertPath"]
            ?? throw new InvalidOperationException("找不到服务端证书路径。");
        var password = bootstrap.Values["Security:ServerCertificate:Password"]
            ?? throw new InvalidOperationException("找不到服务端证书保护口令。");
        using var certificate = ServerCertificateFactory.CreateAndWrite(
            path,
            password,
            protectTemporaryFile: options.EnforceAcl
                ? path => SecureFileSystem.ApplyFileAcl(path)
                : null);
        if (options.EnforceAcl)
            ApplySecretAcl(path);
        return ServerCertificateFactory.GetFingerprint(certificate);
    }

    private static LocalServerSecrets? ReadSecrets(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LocalServerSecrets>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                ?? throw new InvalidOperationException("server-secrets.json 内容为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"服务端密钥文件格式无效：{path}", ex);
        }
    }

    private static bool SecretsEqual(LocalServerSecrets left, LocalServerSecrets right) =>
        JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    private static void WriteSecretsAtomically(string path, LocalServerSecrets secrets, bool enforceAcl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (File.Create(temp))
        {
        }
        if (enforceAcl)
            SecureFileSystem.ApplyFileAcl(temp);
        File.WriteAllText(temp, JsonSerializer.Serialize(secrets, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, true);
        // 先原子替换，再收紧最终路径 ACL；避免受保护临时文件在 Windows 上无法移动。
        if (enforceAcl)
            ApplySecretAcl(path);
    }

    private static CrossProcessLock AcquireCrossProcessLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                return new CrossProcessLock(lockPath, new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"无法获取服务端引导锁：{lockPath}。请确认没有其他 BackupMonitor 进程正在启动；若确认没有持有者，请删除该锁文件后重试。",
                    ex);
            }
        }
    }

    private sealed class CrossProcessLock : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _stream;

        public CrossProcessLock(string path, FileStream stream)
        {
            _path = path;
            _stream = stream;
        }

        public void Dispose()
        {
            _stream.Dispose();
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // 下一次启动会在超时后报告真实锁文件问题。
            }
        }
    }

    private static void ApplySecretAcl(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        SecureFileSystem.ApplyFileAcl(path);
    }

    private static void GenerateClientCa(string path, string password, bool enforceAcl)
    {
        CreateDirectory(Path.GetDirectoryName(path)!, enforceAcl);
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=BackupMonitor Local Client CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        var temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (File.Create(temporaryPath))
        {
        }
        if (enforceAcl)
            SecureFileSystem.ApplyFileAcl(temporaryPath);
        try
        {
            File.WriteAllBytes(temporaryPath, certificate.Export(X509ContentType.Pfx, password));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // 保留原始异常。
            }
        }
    }

    private static void CreateDirectory(string path, bool enforceAcl)
    {
        if (enforceAcl)
            SecureFileSystem.CreateDirectory(path);
        else
            Directory.CreateDirectory(path);
    }

    private static string CreateRsaPrivateKey()
    {
        using var rsa = RSA.Create(3072);
        return Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
    }

    private static string NewSecret(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    private static int ParsePort(string? value, int fallback) =>
        int.TryParse(value, out var port) && port is > 0 and <= 65535 ? port : fallback;

    private static bool? ParseBool(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : bool.TryParse(value, out var result) ? result : null;

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var result)
            ? result
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool IsTrue(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}

public sealed class LocalServerBootstrapOptions
{
    public bool? Enabled { get; init; }
    public string? DataDirectory { get; init; }
    public string? SecretsPath { get; init; }
    public bool AllowCaRegeneration { get; init; }
    public bool EnforceAcl { get; init; } = true;
    /// <summary>仅安装器初始化数据库时临时暴露超级用户口令；API 启动不得开启。</summary>
    public bool IncludeInstallationSecrets { get; init; }
    public bool DiscoveryEnabled { get; init; } = true;
    public int DiscoveryPort { get; init; } = 45808;
}

public sealed class LocalServerBootstrapResult
{
    internal LocalServerBootstrapResult(
        bool enabled,
        string dataDirectory,
        string secretsPath,
        IReadOnlyDictionary<string, string?> values,
        string? instanceId)
    {
        Enabled = enabled;
        DataDirectory = dataDirectory;
        SecretsPath = secretsPath;
        Values = values;
        InstanceId = instanceId;
    }

    public bool Enabled { get; }
    public string DataDirectory { get; }
    public string SecretsPath { get; }
    public IReadOnlyDictionary<string, string?> Values { get; }
    public string? InstanceId { get; }

    internal static LocalServerBootstrapResult Disabled(string dataDirectory, string secretsPath) =>
        new(false, dataDirectory, secretsPath, new Dictionary<string, string?>(), null);

}

public sealed class LocalServerSecrets
{
    public int SchemaVersion { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DeploymentMode { get; set; } = "LanSimple";
    public bool? AutomaticEnrollment { get; set; }
    public bool? PrivateNetworkOnly { get; set; }
    public DateTime? EnrollmentOpenUntilUtc { get; set; }
    public string JwtSigningKey { get; set; } = string.Empty;
    public string CommandSigningPrivateKey { get; set; } = string.Empty;
    public string ClientCaPassword { get; set; } = string.Empty;
    public string ServerCertificatePassword { get; set; } = string.Empty;
    public string PostgresPassword { get; set; } = string.Empty;
    public string PostgresSuperuserPassword { get; set; } = string.Empty;
    public int PostgresPort { get; set; }
    public string? ExplicitConnectionString { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
