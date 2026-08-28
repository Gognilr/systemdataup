using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using BackupMonitor.Api.Bootstrap;
using BackupMonitor.Infrastructure.Common;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BackupMonitor.Server.Setup;

internal sealed record ServerInstallRequest(
    string? AdminPassword,
    string InstallDirectory,
    string DataDirectory,
    int ApiPort = 5080,
    int PostgresPort = 55432,
    int? HttpPort = null,
    string? DisplayName = null);

internal sealed record ServerInstallProgress(string Message, int Percent);

internal sealed class ServerInstaller
{
    public const string ServiceName = "BackupMonitor.Server";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task InstallAsync(
        ServerInstallRequest request,
        IProgress<ServerInstallProgress> progress,
        CancellationToken ct)
    {
        var firstInstall = !IsInstalled();
        Validate(request, firstInstall);
        EnsureAdministrator();

        var payloadTemp = Path.Combine(Path.GetTempPath(), "BackupMonitor.Server.Setup", Guid.NewGuid().ToString("N"));
        var appExisted = Directory.Exists(request.InstallDirectory);
        var apiServiceExisted = ServiceExists(ServiceName);
        var postgresServiceExisted = PostgreSqlManager.ServiceExists(PostgreSqlManager.ServiceName);
        var serviceCreated = false;
        try
        {
            Directory.CreateDirectory(payloadTemp);
            progress.Report(new ServerInstallProgress("正在读取安装 payload…", 5));
            var payloadRoot = await ExtractPayloadAsync(payloadTemp, ct);
            ValidatePayload(payloadRoot);

            Directory.CreateDirectory(request.InstallDirectory);
            Directory.CreateDirectory(request.DataDirectory);
            // 覆盖安装时上一次装的服务往往还在跑，Windows 会锁住已加载的
            // BackupMonitor.Api.dll，下面的复制必定撞共享冲突。PostgreSQL 运行时靠
            // 「已完整就跳过复制」绕开了同一问题，但 API 目录每次都必须真的覆盖，
            // 只能先把服务停下来。
            if (apiServiceExisted)
            {
                progress.Report(new ServerInstallProgress("正在停止已有的服务端服务…", 10));
                await StopApiServiceAsync(ct);
            }

            progress.Report(new ServerInstallProgress("正在复制服务端和 PostgreSQL 运行时…", 15));
            var postgresRuntime = Path.Combine(request.InstallDirectory, "PostgreSQL");
            // Payload extraction and recursive copying can process thousands of
            // files. Keep that synchronous filesystem work off the WinForms UI
            // thread so Windows does not mark the installer as unresponsive.
            await Task.Run(
                () =>
                {
                    CopyDirectory(Path.Combine(payloadRoot, "api"), request.InstallDirectory);
                    EnsurePostgreSqlRuntime(
                        Path.Combine(payloadRoot, "postgresql"),
                        postgresRuntime);
                },
                ct);

            progress.Report(new ServerInstallProgress("正在生成服务端内部密钥…", 25));
            var bootstrapConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DeploymentMode"] = "LanSimple",
                    ["Server:DataDirectory"] = request.DataDirectory,
                    ["Server:DisplayName"] = request.DisplayName ?? Environment.MachineName,
                    ["LanMode:AdvertisedUrl"] = $"https://{Environment.MachineName}:{request.ApiPort}",
                    ["LanMode:DiscoveryEnabled"] = "true",
                    ["LanMode:DiscoveryPort"] = "45808",
                    ["Postgres:Port"] = request.PostgresPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })
                .Build();
            var bootstrap = LocalServerBootstrap.Initialize(
                bootstrapConfiguration,
                new LocalServerBootstrapOptions
                {
                    Enabled = true,
                    DataDirectory = request.DataDirectory,
                    SecretsPath = Path.Combine(request.DataDirectory, "server-secrets.json"),
                    DiscoveryEnabled = true,
                    DiscoveryPort = 45808,
                    IncludeInstallationSecrets = true
                });
            var postgresPassword = bootstrap.Values["Postgres:Password"]
                ?? throw new InvalidOperationException("服务端引导没有生成 PostgreSQL 口令。");
            var postgresSuperuserPassword = bootstrap.Values["Postgres:SuperuserPassword"]
                ?? throw new InvalidOperationException("服务端引导没有生成 PostgreSQL 超级用户口令。");
            var applicationConnection = bootstrap.Values["ConnectionStrings:Default"]
                ?? throw new InvalidOperationException("服务端引导没有生成应用数据库连接串。");
            var serverCertificateFingerprint = bootstrap.Values["Security:ServerCertificate:Fingerprint"]
                ?? throw new InvalidOperationException("服务端引导没有生成 TLS 证书指纹。");
            WriteTurnkeyConfiguration(request);

            progress.Report(new ServerInstallProgress("正在初始化 BackupMonitor 专用 PostgreSQL…", 35));
            await new PostgreSqlManager().InitializeAsync(
                postgresRuntime,
                PostgreSqlDataDirectory,
                request.PostgresPort,
                postgresSuperuserPassword,
                postgresPassword,
                ct);

            progress.Report(new ServerInstallProgress("正在执行数据库迁移…", 55));
            await new MigrationRunner().ApplyAsync(
                applicationConnection,
                // Migration SQL is immutable installer payload, not runtime data.
                // Read it directly from this run's private extraction directory so
                // a previous partial install cannot block retry by leaving stale,
                // protected files under ProgramData\BackupMonitor\Server\database.
                Path.Combine(payloadRoot, "database"),
                request.AdminPassword,
                new Progress<string>(message => progress.Report(new ServerInstallProgress(message, 55))),
                ct);

            progress.Report(new ServerInstallProgress("正在设置仓库与暂存目录…", 65));
            await ConfigureStoragePathsAsync(applicationConnection, request.DataDirectory, ct);

            progress.Report(new ServerInstallProgress("正在注册 Windows 服务和防火墙规则…", 70));
            await RegisterApiServiceAsync(request.InstallDirectory, ct);
            serviceCreated = !apiServiceExisted;
            await ConfigureFirewallAsync(request.ApiPort, ct);

            progress.Report(new ServerInstallProgress("正在启动服务并检查管理网页…", 82));
            await StartServiceAndWaitAsync(request.ApiPort, applicationConnection, serverCertificateFingerprint, ct);
            progress.Report(new ServerInstallProgress("安装完成，正在打开管理网页…", 100));
            OpenBrowser($"https://{Environment.MachineName}:{request.ApiPort}/");
        }
        catch
        {
            if (serviceCreated)
                await TryDeleteApiServiceAsync(ct);
            // InitializeAsync may register the PostgreSQL service before a
            // later connection/migration step fails. Check actual state so a
            // failed first install never leaks a half-configured service.
            if (!postgresServiceExisted && PostgreSqlManager.ServiceExists(PostgreSqlManager.ServiceName))
                await TryDeleteServiceAsync(PostgreSqlManager.ServiceName, ct);
            if (!appExisted)
                TryDeleteDirectory(request.InstallDirectory);
            throw;
        }
        finally
        {
            TryDeleteDirectory(payloadTemp);
        }
    }

    public static bool IsInstalled() => ServiceExists(ServiceName);

    private static void Validate(ServerInstallRequest request, bool firstInstall)
    {
        if (firstInstall && string.IsNullOrWhiteSpace(request.AdminPassword))
            throw new InvalidOperationException("首次安装必须设置管理员密码。");
        // 与 Web 端自助改密、维护面板的重置口令共用 PasswordPolicy。
        // 原来这里只判长度，"aaaaaaaaaaaa" 能作为初始管理员口令装进去，
        // 而同一个口令在 Web 端改密时会被拒——三条设置口令的路径必须同一套规则。
        if (!string.IsNullOrWhiteSpace(request.AdminPassword))
        {
            var strengthError = PasswordPolicy.Validate(request.AdminPassword, "admin");
            if (strengthError is not null)
                throw new InvalidOperationException(strengthError.Replace("新口令", "管理员密码"));
        }
        if (!firstInstall && !string.IsNullOrWhiteSpace(request.AdminPassword))
            throw new InvalidOperationException("升级/修复不会修改管理员密码；请使用“重置管理员密码”维护操作。");
        if (request.ApiPort is < 1024 or > 65535 || request.PostgresPort is < 1024 or > 65535)
            throw new InvalidOperationException("监听端口必须在 1024 到 65535 之间。");
        if (request.ApiPort == request.PostgresPort)
            throw new InvalidOperationException("API 端口与 PostgreSQL 端口不能相同。");
        if (request.HttpPort is not null && (request.HttpPort < 1024 || request.HttpPort > 65535))
            throw new InvalidOperationException("HTTP 端口必须在 1024 到 65535 之间。");
        if (request.HttpPort == request.ApiPort)
            throw new InvalidOperationException("HTTP 与 HTTPS 端口不得相同。");
    }

    private static void EnsureAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("服务端安装器只能在 Windows 上运行。");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("必须以管理员身份运行服务端安装器。");
    }

    private static async Task<string> ExtractPayloadAsync(string tempRoot, CancellationToken ct)
    {
        var zipPath = Path.Combine(tempRoot, "server-payload.zip");
        await using var stream = FindPayloadStream();
        if (stream is not null)
        {
            await using var file = File.Create(zipPath);
            await stream.CopyToAsync(file, ct);
        }
        else
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "payload", "server-payload.zip");
            if (!File.Exists(fallback))
                throw new FileNotFoundException("安装器没有内置 server-payload.zip；请使用发布打包脚本生成完整安装器。", fallback);
            File.Copy(fallback, zipPath);
        }

        var root = Path.Combine(tempRoot, "payload");
        // ZipFile has no asynchronous extraction API. Run it on a worker thread
        // to keep the installer window repainting while the large PostgreSQL
        // payload is expanded.
        await Task.Run(
            () => ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true),
            ct);
        return root;
    }

    private static Stream? FindPayloadStream()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("server-payload.zip", StringComparison.OrdinalIgnoreCase));
        return resource is null ? null : assembly.GetManifestResourceStream(resource);
    }

    private static void ValidatePayload(string root)
    {
        if (!File.Exists(Path.Combine(root, "api", "BackupMonitor.Api.exe")))
            throw new InvalidOperationException("服务端 payload 缺少 BackupMonitor.Api.exe。");
        if (!File.Exists(Path.Combine(root, "database", "V001__initial_schema.sql")))
            throw new InvalidOperationException("服务端 payload 缺少数据库迁移 V001。");
        if (!Directory.Exists(Path.Combine(root, "postgresql", "bin")))
            throw new InvalidOperationException("服务端 payload 缺少 PostgreSQL Windows 运行时目录。");
    }

    /// <summary>
    /// 把仓库与暂存目录落到本次安装选定的数据目录下。
    ///
    /// 一键安装已经问过数据目录，就该由它给出权威取值。不这么做的话，取值会落到
    /// V001 的种子上——那是开发机的 E:\BackupRepository / D:\BackupStaging，
    /// 目标机上多半没有这两个盘，运行期解析仓库根就会抛异常。
    ///
    /// 只在取值仍是「未配置」或仍等于旧种子时才写：管理员显式配置过的路径不能覆盖。
    /// </summary>
    private static async Task ConfigureStoragePathsAsync(
        string connectionString,
        string dataDirectory,
        CancellationToken ct)
    {
        var repository = Path.Combine(dataDirectory, "repository");
        var staging = Path.Combine(dataDirectory, "staging");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(staging);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await SetStoragePathAsync(connection, "repository_path", @"E:\BackupRepository", repository, ct);
        await SetStoragePathAsync(connection, "staging_path", @"D:\BackupStaging", staging, ct);
    }

    private static async Task SetStoragePathAsync(
        NpgsqlConnection connection,
        string key,
        string seededValue,
        string newValue,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE system_settings
               SET setting_value = to_jsonb(@value::text),
                   updated_at    = now(),
                   row_version   = row_version + 1
             WHERE setting_key = @key
               AND (setting_value = 'null'::jsonb OR setting_value #>> '{}' = @seeded)
            """,
            connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", newValue);
        command.Parameters.AddWithValue("seeded", seededValue);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void WriteTurnkeyConfiguration(ServerInstallRequest request)
    {
        var path = Path.Combine(request.InstallDirectory, "appsettings.Turnkey.json");
        var content = new Dictionary<string, object?>
        {
            ["DeploymentMode"] = "LanSimple",
            ["Server"] = new Dictionary<string, object?>
            {
                ["DataDirectory"] = request.DataDirectory,
                ["DisplayName"] = request.DisplayName ?? Environment.MachineName,
                ["AdvertisedUrl"] = $"https://{Environment.MachineName}:{request.ApiPort}",
                ["ApiPort"] = request.ApiPort
            },
            ["LanMode"] = new Dictionary<string, object?>
            {
                ["AutomaticEnrollment"] = true,
                ["PrivateNetworkOnly"] = true,
                ["DiscoveryEnabled"] = true,
                ["DiscoveryPort"] = 45808,
                ["AdvertisedUrl"] = $"https://{Environment.MachineName}:{request.ApiPort}"
            },
            ["Kestrel"] = new Dictionary<string, object?>
            {
                // 端点由 Program.cs 显式注册；显式 null 标记防止旧配置误认为
                // Turnkey 仍需要 HTTP 端点。Program.cs 同时禁用配置端点加载器，
                // 因而升级时残留的 appsettings.Production:Http 也不会被合并。
                ["Endpoints"] = new Dictionary<string, object?>
                {
                    ["Http"] = null
                }
            },
            ["Storage"] = new Dictionary<string, object?>
            {
                ["RepositoryPath"] = Path.Combine(request.DataDirectory, "Repository"),
                ["StagingPath"] = Path.Combine(request.DataDirectory, "Staging")
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(content, JsonOptions));
    }

    private static async Task RegisterApiServiceAsync(string installDirectory, CancellationToken ct)
    {
        var exePath = Path.Combine(installDirectory, "BackupMonitor.Api.exe");
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        if (ServiceExists(ServiceName))
        {
            await ProcessRunner.RunAsync(sc, ["stop", ServiceName], ct, throwOnError: false);
            await ProcessRunner.RunAsync(sc,
                ["config", ServiceName, "binPath=", $"\"{exePath}\"", "DisplayName=", "BackupMonitor Server",
                 "start=", "delayed-auto", "obj=", "LocalSystem"], ct);
        }
        else
        {
            await ProcessRunner.RunAsync(sc,
                ["create", ServiceName, "binPath=", $"\"{exePath}\"", "DisplayName=", "BackupMonitor Server",
                 "start=", "delayed-auto", "obj=", "LocalSystem"], ct);
        }
        await ProcessRunner.RunAsync(sc,
            ["description", ServiceName, "BackupMonitor LAN server"], ct);
        await ProcessRunner.RunAsync(sc,
            ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"], ct);
        await ProcessRunner.RunAsync(sc,
            ["failureflag", ServiceName, "1"], ct);
    }

    private static async Task ConfigureFirewallAsync(int apiPort, CancellationToken ct)
    {
        // profile 必须是 any，不能是 private。Windows 的网络位置分类是每台机器各自判定的：
        // 新装的 Server 会把「无法识别的网络」归为公用，加域的机器归域网络，两种情况下
        // profile=private 的规则完全不生效。表现是同一网段、同一个客户端，换一台服务器就
        // 「发现不了」——而且被挡住的不只是 UDP 发现，API 的 TCP 端口一样进不来。
        // 规则本身已经按端口收窄，这是局域网内网产品，不靠 profile 再收一道。
        var netsh = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "netsh.exe");
        await ProcessRunner.RunAsync(netsh,
            ["advfirewall", "firewall", "delete", "rule", "name=BackupMonitor Server TCP"], ct, throwOnError: false);
        await ProcessRunner.RunAsync(netsh,
            ["advfirewall", "firewall", "add", "rule", "name=BackupMonitor Server TCP",
              "dir=in", "action=allow", "protocol=TCP", $"localport={apiPort}", "profile=any"], ct);
        await ProcessRunner.RunAsync(netsh,
            ["advfirewall", "firewall", "delete", "rule", "name=BackupMonitor Discovery UDP"], ct, throwOnError: false);
        await ProcessRunner.RunAsync(netsh,
            ["advfirewall", "firewall", "add", "rule", "name=BackupMonitor Discovery UDP",
              "dir=in", "action=allow", "protocol=UDP", "localport=45808", "profile=any"], ct);
    }

    private static async Task StartServiceAndWaitAsync(
        int apiPort,
        string connectionString,
        string serverCertificateFingerprint,
        CancellationToken ct)
    {
        await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["start", ServiceName], ct);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && string.Equals(
                    ServerCertificateFactory.GetFingerprint(certificate),
                    serverCertificateFingerprint,
                    StringComparison.OrdinalIgnoreCase)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var health = await client.GetAsync($"https://127.0.0.1:{apiPort}/health", ct);
                if (health.IsSuccessStatusCode && await DatabaseReadyAsync(connectionString, ct))
                    return;
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                // 服务正在启动。
            }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("BackupMonitor Server 未能在 45 秒内启动并响应 /health。");
    }

    private static async Task<bool> DatabaseReadyAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        catch (NpgsqlException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string PostgreSqlDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BackupMonitor",
        "PostgreSQL",
        "data");

    private static bool ServiceExists(string name)
    {
        try
        {
            using var service = new System.ServiceProcess.ServiceController(name);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 停止 API 服务并等到它真正退出。sc.exe stop 只是把停止请求投递给 SCM 就返回，
    /// 立刻去覆盖文件仍会撞上尚未退出的进程，因此必须轮询到 Stopped 为止。
    /// </summary>
    private static async Task StopApiServiceAsync(CancellationToken ct)
    {
        await ProcessRunner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "sc.exe"),
            ["stop", ServiceName],
            ct,
            throwOnError: false);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var service = new System.ServiceProcess.ServiceController(ServiceName);
                service.Refresh();
                if (service.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                    return;
            }
            catch (InvalidOperationException)
            {
                // 服务已不存在，无需再等。
                return;
            }

            await Task.Delay(500, ct);
        }

        throw new System.TimeoutException(
            $"Windows 服务 {ServiceName} 未能在 30 秒内停止，无法覆盖安装目录中正在使用的文件。"
            + "请手动停止该服务（sc stop " + ServiceName + "）后重试。");
    }

    private static async Task TryDeleteApiServiceAsync(CancellationToken ct)
    {
        await TryDeleteServiceAsync(ServiceName, ct);
    }

    private static async Task TryDeleteServiceAsync(string serviceName, CancellationToken ct)
    {
        await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["stop", serviceName], ct, throwOnError: false);
        await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["delete", serviceName], ct, throwOnError: false);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打开浏览器失败不影响服务安装结果。
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"payload 目录不存在：{source}");
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void EnsurePostgreSqlRuntime(string source, string target)
    {
        // A valid existing runtime may be in use by the PostgreSQL service and
        // Windows locks loaded executables. Reuse it on repair; first installs
        // and interrupted copies are completed from the immutable payload.
        if (IsCompletePostgreSqlRuntime(target))
            return;

        CopyDirectory(source, target);
        if (!IsCompletePostgreSqlRuntime(target))
            throw new InvalidOperationException($"PostgreSQL 运行时复制不完整：{target}");
    }

    private static bool IsCompletePostgreSqlRuntime(string root) =>
        File.Exists(Path.Combine(root, "bin", "initdb.exe"))
        && File.Exists(Path.Combine(root, "bin", "pg_ctl.exe"))
        && File.Exists(Path.Combine(root, "bin", "postgres.exe"))
        && Directory.Exists(Path.Combine(root, "lib"))
        && Directory.Exists(Path.Combine(root, "share"));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 失败安装的清理不覆盖主异常。
        }
    }
}
