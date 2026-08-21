using System.Net.Http;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text.Json;
using BackupMonitor.Api.Bootstrap;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BackupMonitor.Server.Setup;

internal sealed class ServerMaintenance
{
    public const string ServerServiceName = "BackupMonitor.Server";
    public const string PostgreSqlServiceName = "BackupMonitor.PostgreSQL";

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "BackupMonitor",
        "Server");

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BackupMonitor",
        "Server");

    public static string DefaultPostgreSqlDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BackupMonitor",
        "PostgreSQL");

    public async Task ResetAdminPasswordAsync(
        string connectionString,
        string newPassword,
        CancellationToken ct)
    {
        if (newPassword.Length < 12)
            throw new InvalidOperationException("管理员密码至少需要 12 个字符。");

        var hash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var update = new NpgsqlCommand(
            "UPDATE users SET password_hash=@hash, password_changed_at=now(), must_change_password=false, updated_at=now() WHERE username='admin'",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("hash", hash);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("找不到 admin 账户，请先完成服务端初始化。");
        }

        await using (var revoke = new NpgsqlCommand(
            "UPDATE refresh_tokens SET revoked_at=now(), revoke_reason='maintenance_password_reset' WHERE revoked_at IS NULL",
            connection,
            transaction))
            await revoke.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<string> ReissueServerCertificateAsync(
        string installDirectory,
        string dataDirectory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var configPath = Path.Combine(installDirectory, "appsettings.Turnkey.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();
        var fingerprint = LocalServerBootstrap.ReissueServerCertificate(
            configuration,
            new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                SecretsPath = Path.Combine(dataDirectory, "server-secrets.json"),
                DiscoveryEnabled = configuration.GetValue("LanMode:DiscoveryEnabled", true),
                DiscoveryPort = configuration.GetValue("LanMode:DiscoveryPort", 45808)
            });
        await RestartServicesAsync(ct);
        return fingerprint;
    }

    public string RepairPermissions(string dataDirectory)
    {
        var postgresDataDirectory = DefaultPostgreSqlDataDirectory;
        foreach (var directory in new[]
        {
            dataDirectory,
            Path.Combine(dataDirectory, "Repository"),
            Path.Combine(dataDirectory, "Staging"),
            postgresDataDirectory
        })
            SecureFileSystem.CreateDirectory(directory);

        foreach (var file in new[]
        {
            Path.Combine(dataDirectory, "server-secrets.json"),
            Path.Combine(dataDirectory, "client-ca.pfx"),
            Path.Combine(dataDirectory, "server-certificate.pfx")
        })
            SecureFileSystem.ApplyFileAcl(file);

        return "服务端数据目录、Repository、Staging、PostgreSQL data 以及现有密钥文件的 ACL 已重新收紧为 SYSTEM + Administrators。";
    }

    public async Task ExportKeyPackageAsync(
        string dataDirectory,
        string packagePath,
        CancellationToken ct)
    {
        var files = GetKeyPackageFiles(dataDirectory);
        EnsureKeyPackageFilesExist(files);

        var targetPath = Path.GetFullPath(packagePath);
        if (GetKeyPackageFiles(dataDirectory).Any(file =>
                string.Equals(file.Path, targetPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("密钥包不能覆盖当前 server-secrets.json、client-ca.pfx 或 server-certificate.pfx。");
        var parent = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("密钥包目标路径无效。");
        Directory.CreateDirectory(parent);
        var tempPath = targetPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (File.Create(tempPath))
            {
            }
            SecureFileSystem.ApplyFileAcl(tempPath);
            await using (var output = new FileStream(
                tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1024 * 64, useAsync: true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (entryName, sourcePath) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    await using var input = new FileStream(
                        sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, useAsync: true);
                    await using var entryStream = entry.Open();
                    await input.CopyToAsync(entryStream, ct);
                }
            }

            File.Move(tempPath, targetPath, true);
            SecureFileSystem.ApplyFileAcl(targetPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public async Task ImportKeyPackageAsync(
        string dataDirectory,
        string packagePath,
        CancellationToken ct)
    {
        var sourcePath = Path.GetFullPath(packagePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到服务端密钥包。", sourcePath);

        var importDirectory = Path.Combine(dataDirectory, $".key-package-import-{Guid.NewGuid():N}");
        SecureFileSystem.CreateDirectory(importDirectory);
        try
        {
            await ExtractAndValidateKeyPackageAsync(sourcePath, importDirectory, ct);
            var files = GetKeyPackageFiles(importDirectory);
            var targets = GetKeyPackageFiles(dataDirectory);
            var backups = new List<(string Target, string Backup, bool Existed)>();
            try
            {
                foreach (var (_, target) in targets)
                {
                    var backup = target + $".{Environment.ProcessId}.{Guid.NewGuid():N}.import-backup";
                    var existed = File.Exists(target);
                    if (existed)
                    {
                        File.Copy(target, backup, true);
                        SecureFileSystem.ApplyFileAcl(backup);
                    }
                    backups.Add((target, backup, existed));
                }

                foreach (var (entryName, source) in files)
                {
                    var target = targets.First(item => item.EntryName == entryName).Path;
                    ReplaceProtectedFile(source, target);
                }
            }
            catch
            {
                foreach (var backup in backups)
                {
                    if (backup.Existed && File.Exists(backup.Backup))
                        ReplaceProtectedFile(backup.Backup, backup.Target);
                    else if (!backup.Existed && File.Exists(backup.Target))
                        TryDelete(backup.Target);
                }
                throw;
            }
            finally
            {
                foreach (var (_, backup, _) in backups)
                    TryDelete(backup);
            }
        }
        finally
        {
            TryDeleteDirectory(importDirectory);
        }

        await RestartServicesAsync(ct);
    }

    public string GetInstalledConnectionString(string installDirectory, string dataDirectory)
    {
        var configPath = Path.Combine(installDirectory, "appsettings.Turnkey.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .Build();
        var bootstrap = LocalServerBootstrap.Initialize(
            configuration,
            new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                SecretsPath = Path.Combine(dataDirectory, "server-secrets.json"),
                DiscoveryEnabled = configuration.GetValue("LanMode:DiscoveryEnabled", true),
                DiscoveryPort = configuration.GetValue("LanMode:DiscoveryPort", 45808)
            });

        return bootstrap.Values["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException("找不到已安装服务端的数据库连接配置。");
    }

    public int GetInstalledApiPort(string installDirectory)
    {
        var configPath = Path.Combine(installDirectory, "appsettings.Turnkey.json");
        if (!File.Exists(configPath))
            return 5080;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var url = document.RootElement
                .GetProperty("Kestrel")
                .GetProperty("Endpoints")
                .GetProperty("Https")
                .GetProperty("Url")
                .GetString();
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Port : 5080;
        }
        catch (Exception)
        {
            return 5080;
        }
    }

    public async Task RestartServicesAsync(CancellationToken ct)
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        await ProcessRunner.RunAsync(sc, ["stop", ServerServiceName], ct, throwOnError: false);
        await WaitForServiceStatusAsync(ServerServiceName, ServiceControllerStatus.Stopped, ct, required: false);
        await ProcessRunner.RunAsync(sc, ["stop", PostgreSqlServiceName], ct, throwOnError: false);
        await WaitForServiceStatusAsync(PostgreSqlServiceName, ServiceControllerStatus.Stopped, ct, required: false);
        await ProcessRunner.RunAsync(sc, ["start", PostgreSqlServiceName], ct);
        await WaitForServiceStatusAsync(PostgreSqlServiceName, ServiceControllerStatus.Running, ct);
        await ProcessRunner.RunAsync(sc, ["start", ServerServiceName], ct);
        await WaitForServiceStatusAsync(ServerServiceName, ServiceControllerStatus.Running, ct);
    }

    public async Task<string> GetDiagnosticsAsync(
        string installDirectory,
        string dataDirectory,
        CancellationToken ct)
    {
        var apiPort = GetInstalledApiPort(installDirectory);
        var serverStatus = GetServiceStatus(ServerServiceName);
        var postgresStatus = GetServiceStatus(PostgreSqlServiceName);
        var secretPath = Path.Combine(dataDirectory, "server-secrets.json");
        var apiStatus = "unreachable";
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(installDirectory, "appsettings.Turnkey.json"), optional: false, reloadOnChange: false)
                .Build();
            var bootstrap = LocalServerBootstrap.Initialize(
                configuration,
                new LocalServerBootstrapOptions
                {
                    Enabled = true,
                    DataDirectory = dataDirectory,
                    SecretsPath = Path.Combine(dataDirectory, "server-secrets.json"),
                    DiscoveryEnabled = configuration.GetValue("LanMode:DiscoveryEnabled", true),
                    DiscoveryPort = configuration.GetValue("LanMode:DiscoveryPort", 45808)
                });
            var expectedFingerprint = bootstrap.Values["Security:ServerCertificate:Fingerprint"];
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(
                        ServerCertificateFactory.GetFingerprint(new X509Certificate2(certificate)),
                        expectedFingerprint,
                        StringComparison.OrdinalIgnoreCase)
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync($"https://127.0.0.1:{apiPort}/health", ct);
            apiStatus = $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException or InvalidOperationException)
        {
            apiStatus = "unreachable";
        }

        return string.Join(Environment.NewLine,
            $"服务端服务：{serverStatus}",
            $"PostgreSQL 服务：{postgresStatus}",
            $"API /health：{apiStatus}",
            $"内部密钥文件：{(File.Exists(secretPath) ? "存在" : "缺失")}",
            $"API 端口：{apiPort}",
            $"检查时间（UTC）：{DateTime.UtcNow:O}");
    }

    public async Task UninstallAsync(
        string installDirectory,
        string dataDirectory,
        bool removeData,
        CancellationToken ct)
    {
        EnsureSafePath(installDirectory, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BackupMonitor"));
        EnsureSafePath(dataDirectory, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMonitor"));
        var postgresDataDirectory = DefaultPostgreSqlDataDirectory;
        EnsureSafePath(postgresDataDirectory, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMonitor"));

        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        await ProcessRunner.RunAsync(sc, ["stop", ServerServiceName], ct, throwOnError: false);
        await ProcessRunner.RunAsync(sc, ["stop", PostgreSqlServiceName], ct, throwOnError: false);
        await ProcessRunner.RunAsync(sc, ["delete", ServerServiceName], ct, throwOnError: false);
        await ProcessRunner.RunAsync(sc, ["delete", PostgreSqlServiceName], ct, throwOnError: false);

        var netsh = Path.Combine(
            Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "netsh.exe");
        await ProcessRunner.RunAsync(netsh,
            ["advfirewall", "firewall", "delete", "rule", "name=BackupMonitor Server TCP"],
            ct,
            throwOnError: false);
        await ProcessRunner.RunAsync(netsh,
            ["advfirewall", "firewall", "delete", "rule", "name=BackupMonitor Discovery UDP"],
            ct,
            throwOnError: false);

        if (Directory.Exists(installDirectory))
            Directory.Delete(installDirectory, recursive: true);
        if (removeData && Directory.Exists(dataDirectory))
            Directory.Delete(dataDirectory, recursive: true);
        if (removeData && Directory.Exists(postgresDataDirectory))
            Directory.Delete(postgresDataDirectory, recursive: true);
    }

    private sealed record KeyPackageFile(string EntryName, string Path);

    private static KeyPackageFile[] GetKeyPackageFiles(string dataDirectory) =>
    [
        new("server-secrets.json", Path.Combine(dataDirectory, "server-secrets.json")),
        new("client-ca.pfx", Path.Combine(dataDirectory, "client-ca.pfx")),
        new("server-certificate.pfx", Path.Combine(dataDirectory, "server-certificate.pfx"))
    ];

    private static void EnsureKeyPackageFilesExist(IEnumerable<KeyPackageFile> files)
    {
        foreach (var file in files)
        {
            if (!File.Exists(file.Path))
                throw new InvalidOperationException($"密钥包缺少服务端文件：{file.Path}");
        }
    }

    private static async Task ExtractAndValidateKeyPackageAsync(
        string packagePath,
        string importDirectory,
        CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var expectedNames = new[] { "server-secrets.json", "client-ca.pfx", "server-certificate.pfx" };
        if (archive.Entries.Count != expectedNames.Length
            || archive.Entries.Any(entry => !expectedNames.Contains(entry.FullName, StringComparer.Ordinal)))
            throw new InvalidOperationException("密钥包内容不完整或包含未识别文件；必须同时包含 server-secrets.json、client-ca.pfx、server-certificate.pfx。");

        foreach (var name in expectedNames)
        {
            ct.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(name)
                ?? throw new InvalidOperationException($"密钥包缺少 {name}。");
            var target = Path.Combine(importDirectory, name);
            await using var input = entry.Open();
            await using var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);
            await input.CopyToAsync(output, ct);
            SecureFileSystem.ApplyFileAcl(target);
        }

        var secretPath = Path.Combine(importDirectory, "server-secrets.json");
        var secrets = JsonSerializer.Deserialize<LocalServerSecrets>(
            await File.ReadAllTextAsync(secretPath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("密钥包中的 server-secrets.json 无法读取。");
        if (string.IsNullOrWhiteSpace(secrets.ClientCaPassword)
            || string.IsNullOrWhiteSpace(secrets.ServerCertificatePassword))
            throw new InvalidOperationException("密钥包中的服务端密钥口令不完整。");

        using (var ca = new X509Certificate2(
                   Path.Combine(importDirectory, "client-ca.pfx"),
                   secrets.ClientCaPassword,
                   X509KeyStorageFlags.EphemeralKeySet))
        {
            if (!ca.HasPrivateKey)
                throw new InvalidOperationException("密钥包中的 client-ca.pfx 不包含私钥。");
        }

        using var serverCertificate = ServerCertificateFactory.Load(
            Path.Combine(importDirectory, "server-certificate.pfx"),
            secrets.ServerCertificatePassword);
    }

    private static void ReplaceProtectedFile(string source, string target)
    {
        var temporaryPath = target + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (File.Create(temporaryPath))
            {
            }
            SecureFileSystem.ApplyFileAcl(temporaryPath);
            File.Copy(source, temporaryPath, true);
            File.Move(temporaryPath, target, true);
            SecureFileSystem.ApplyFileAcl(target);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 导入临时目录清理失败不覆盖主流程；下次维护操作会再次清理。
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
            // 不在日志中输出密钥文件路径或内容。
        }
    }

    private static string GetServiceStatus(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "未安装";
        }
    }

    private static async Task WaitForServiceStatusAsync(
        string name,
        ServiceControllerStatus desired,
        CancellationToken ct,
        bool required = true)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var service = new ServiceController(name);
                service.Refresh();
                if (service.Status == desired)
                    return;
            }
            catch (InvalidOperationException) when (!required)
            {
                return;
            }

            await Task.Delay(250, ct);
        }

        if (required)
            throw new System.TimeoutException($"Windows 服务 {name} 未能在 30 秒内进入 {desired} 状态。");
    }

    private static void EnsureSafePath(string path, string allowedRoot)
    {
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("维护操作的目录不在 BackupMonitor 专用目录内。");
    }
}
