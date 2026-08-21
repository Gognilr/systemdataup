using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.ServiceProcess;
using BackupMonitor.Shared.Security;
using Microsoft.Win32;

namespace BackupMonitor.Agent.Setup;

internal sealed record InstallProgress(string Message, int Percent);

internal sealed record AgentBootstrap(
    string ServerSigningPublicKey,
    string AgentPackagePath,
    bool AutomaticEnrollment,
    string DeploymentMode,
    string? ServerCertificateFingerprint,
    string? ServerCertificatePem);

/// <summary>
/// 无 PowerShell 依赖的客户端安装逻辑。安装器本身由 UAC manifest 请求管理员权限，
/// 通过 Windows 原生 sc.exe、注册表和文件 API 创建服务及托盘自启动。
/// </summary>
internal sealed class AgentInstaller
{
    private const string ServiceName = "BackupMonitor Agent";
    private const string InstallDirectory = @"%ProgramFiles%\BackupMonitor\Agent";
    private const string DataDirectory = @"%ProgramData%\BackupMonitor\Agent";
    private const string RunValueName = "BackupMonitor.Agent.Tray";

    public static bool IsInstalled()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<AgentBootstrap> GetBootstrapAsync(string serverUrl, CancellationToken ct)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        using var client = CreateHttpClient();
        using var response = await client.GetAsync(new Uri(new Uri(baseUrl + "/"), "api/v1/agent/bootstrap"), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractApiError(body, $"服务端返回 HTTP {(int)response.StatusCode}"));

        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.TryGetProperty("data", out var wrapped)
            ? wrapped
            : document.RootElement;
        var publicKey = data.TryGetProperty("serverSigningPublicKey", out var key)
            ? key.GetString()
            : null;
        var packagePath = data.TryGetProperty("agentPackagePath", out var package)
            ? package.GetString()
            : null;
        var automaticEnrollment = data.TryGetProperty("automaticEnrollment", out var automatic)
            && automatic.ValueKind == JsonValueKind.True;
        var deploymentMode = data.TryGetProperty("deploymentMode", out var mode)
            ? mode.GetString() ?? "Secure"
            : "Secure";
        var certificateFingerprint = data.TryGetProperty("serverCertificateFingerprint", out var fingerprint)
            ? fingerprint.GetString()
            : null;
        var certificatePem = data.TryGetProperty("serverCertificatePem", out var pem)
            ? pem.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(publicKey))
            throw new InvalidOperationException("服务端没有提供 Agent 指令签名公钥，请检查服务端密钥配置。");

        if (string.Equals(deploymentMode, "LanSimple", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(certificateFingerprint))
            throw new InvalidOperationException("服务端没有提供 TLS 证书指纹，请检查 Turnkey 服务端证书配置。");

        ValidatePublicKey(publicKey);
        return new AgentBootstrap(
            publicKey,
            string.IsNullOrWhiteSpace(packagePath) ? "/downloads/BackupMonitor.Agent.zip" : packagePath,
            automaticEnrollment,
            deploymentMode,
            certificateFingerprint,
            certificatePem);
    }

    public async Task InstallAsync(
        string serverUrl,
        string displayName,
        string registrationToken,
        IProgress<InstallProgress> progress,
        CancellationToken ct)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        progress.Report(new InstallProgress("正在读取服务端安装配置…", 5));
        var bootstrap = await GetBootstrapAsync(baseUrl, ct);
        if (!bootstrap.AutomaticEnrollment && string.IsNullOrWhiteSpace(registrationToken))
            throw new InvalidOperationException("当前服务端是 Secure 模式，请在“高级选项”中填写注册令牌。");
        var packageUri = ResolvePackageUri(baseUrl, bootstrap.AgentPackagePath);

        var tempRoot = Path.Combine(Path.GetTempPath(), "BackupMonitor.Agent.Setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var packagePath = Path.Combine(tempRoot, "BackupMonitor.Agent.zip");
            progress.Report(new InstallProgress("正在下载客户端组件…", 10));
            await DownloadPackageAsync(packageUri, packagePath, progress, ct);

            var payloadRoot = Path.Combine(tempRoot, "payload");
            progress.Report(new InstallProgress("正在解压客户端组件…", 62));
            ZipFile.ExtractToDirectory(packagePath, payloadRoot, overwriteFiles: true);
            ValidatePayload(payloadRoot);

            await Task.Run(() =>
            {
                progress.Report(new InstallProgress("正在停止旧版本服务…", 68));
                StopAndDeleteExistingService();

                progress.Report(new InstallProgress("正在复制客户端文件…", 73));
                var installDir = Expand(InstallDirectory);
                var dataDir = Expand(DataDirectory);
                Directory.CreateDirectory(installDir);
                SecureFileSystem.CreateDirectory(dataDir);
                CopyPayload(payloadRoot, installDir);

                progress.Report(new InstallProgress("正在写入安全配置…", 80));
                WriteAgentSettings(
                    installDir,
                    baseUrl,
                    bootstrap.ServerSigningPublicKey,
                    bootstrap.ServerCertificateFingerprint,
                    bootstrap.AutomaticEnrollment,
                    bootstrap.DeploymentMode,
                    displayName,
                    dataDir,
                    registrationToken);

                progress.Report(new InstallProgress("正在注册 Windows 服务…", 86));
                CreateService(installDir);
                ConfigureTrayStartup(installDir, baseUrl, dataDir);

                progress.Report(new InstallProgress("正在启动 Agent…", 94));
                StartServiceAndWait();
                StartTray(installDir, baseUrl, dataDir);
                SaveLastServerUrl(baseUrl);
            }, ct);

            progress.Report(new InstallProgress(
                bootstrap.AutomaticEnrollment ? "安装完成，正在自动登记…" : "安装完成，等待高级安全模式审批…",
                100));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public async Task UninstallAsync(bool removeData, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            StopTray();
            StopAndDeleteExistingService();

            using (var runKey = Registry.CurrentUser.OpenSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\BackupMonitor\AgentSetup", throwOnMissingSubKey: false);

            DeleteDirectorySafely(Expand(InstallDirectory), Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BackupMonitor"));
            if (removeData)
                DeleteDirectorySafely(Expand(DataDirectory), Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMonitor"));
        }, ct);
    }

    private static HttpClient CreateHttpClient() => new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private static string NormalizeServerUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("服务端地址必须是有效的 HTTP 或 HTTPS 地址。");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static Uri ResolvePackageUri(string serverUrl, string packagePath)
    {
        if (Uri.TryCreate(packagePath, UriKind.Absolute, out var absolute))
            return absolute;
        return new Uri(new Uri(serverUrl + "/"), packagePath.TrimStart('/'));
    }

    private static async Task DownloadPackageAsync(
        Uri packageUri,
        string packagePath,
        IProgress<InstallProgress> progress,
        CancellationToken ct)
    {
        using var client = CreateHttpClient();
        using var response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"客户端组件下载失败，服务端返回 HTTP {(int)response.StatusCode}。");

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);
        var buffer = new byte[1024 * 128];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            var percent = total is > 0
                ? 10 + (int)Math.Clamp(readTotal * 50 / total.Value, 0, 50)
                : 25;
            progress.Report(new InstallProgress("正在下载客户端组件…", percent));
        }
    }

    private static void ValidatePublicKey(string value)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("服务端返回的 Agent 签名公钥格式无效。", ex);
        }
    }

    private static void ValidatePayload(string payloadRoot)
    {
        foreach (var name in new[] { "BackupMonitor.Agent.exe", "BackupMonitor.Agent.Tray.exe", "appsettings.json" })
        {
            if (!File.Exists(Path.Combine(payloadRoot, name)))
                throw new InvalidOperationException($"服务端安装包缺少文件：{name}");
        }
    }

    private static void CopyPayload(string payloadRoot, string installDir)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(sourcePath);
            if (name.Equals("install-agent.ps1", StringComparison.OrdinalIgnoreCase)
                || name.Equals("uninstall-agent.ps1", StringComparison.OrdinalIgnoreCase)
                || name.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(sourcePath, Path.Combine(installDir, name), overwrite: true);
        }
    }

    private static void WriteAgentSettings(
        string installDir,
        string serverUrl,
        string publicKey,
        string? serverCertificateFingerprint,
        bool automaticEnrollment,
        string deploymentMode,
        string displayName,
        string dataDir,
        string registrationToken)
    {
        var settingsPath = Path.Combine(installDir, "appsettings.json");
        var root = File.Exists(settingsPath)
            ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var agent = root["Agent"] as JsonObject ?? new JsonObject();
        root["Agent"] = agent;
        agent["ServerUrl"] = serverUrl;
        agent["AutomaticEnrollment"] = automaticEnrollment;
        agent["DeploymentMode"] = deploymentMode;
        agent["RegistrationToken"] = string.Empty;
        agent["ServerSigningPublicKey"] = publicKey;
        agent["ServerCertificateFingerprint"] = serverCertificateFingerprint ?? string.Empty;
        agent["AllowUnsignedCommands"] = false;
        agent["DisplayName"] = displayName;
        agent["DataDirectory"] = dataDir;
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        WriteProtectedRegistrationToken(dataDir, registrationToken);
    }

    private static void WriteProtectedRegistrationToken(string dataDir, string token)
    {
        var tokenPath = Path.Combine(dataDir, "registration-token.txt");
        if (string.IsNullOrWhiteSpace(token))
        {
            if (File.Exists(tokenPath))
                File.Delete(tokenPath);
            return;
        }

        File.WriteAllText(tokenPath, token.Trim(), new UTF8Encoding(false));
        SecureFileSystem.ApplyFileAcl(tokenPath);
    }

    private static void CreateService(string installDir)
    {
        var exePath = Path.Combine(installDir, "BackupMonitor.Agent.exe");
        RunSc("create", ServiceName,
            "binPath=", $"\"{exePath}\"",
            "DisplayName=", ServiceName,
            "start=", "delayed-auto",
            "obj=", "LocalSystem");
        RunSc("description", ServiceName, "BackupMonitor Agent data collection service");
        RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        RunSc("failureflag", ServiceName, "1");
    }

    private static void ConfigureTrayStartup(string installDir, string serverUrl, string dataDir)
    {
        var trayPath = Path.Combine(installDir, "BackupMonitor.Agent.Tray.exe");
        var arguments = BuildTrayArguments(serverUrl, dataDir);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key?.SetValue(RunValueName, $"\"{trayPath}\" {arguments}", RegistryValueKind.String);
    }

    private static void StartServiceAndWait()
    {
        RunSc("start", ServiceName);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Running)
                    return;
            }
            catch (InvalidOperationException)
            {
                // SCM 可能还在创建服务，继续等待。
            }
            Thread.Sleep(500);
        }
        throw new InvalidOperationException("Agent 服务未能在规定时间内启动，请查看 Windows 服务和事件日志。");
    }

    private static void StartTray(string installDir, string serverUrl, string dataDir)
    {
        var trayPath = Path.Combine(installDir, "BackupMonitor.Agent.Tray.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = trayPath,
            Arguments = BuildTrayArguments(serverUrl, dataDir),
            UseShellExecute = true
        });
    }

    private static void StopTray()
    {
        foreach (var process in Process.GetProcessesByName("BackupMonitor.Agent.Tray"))
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000))
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 卸载继续执行；若其他用户的托盘仍在运行，目录删除会给出明确错误。
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string BuildTrayArguments(string serverUrl, string dataDir) =>
        $"--service-name \"{EscapeArgument(ServiceName)}\" --server-url \"{EscapeArgument(serverUrl)}\" --data-directory \"{EscapeArgument(dataDir)}\"";

    private static string EscapeArgument(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void StopAndDeleteExistingService()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        RunSc("delete", ServiceName);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                service.Refresh();
            }
            catch (InvalidOperationException)
            {
                return;
            }
            Thread.Sleep(250);
        }
    }

    private static void SaveLastServerUrl(string serverUrl)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\BackupMonitor\AgentSetup");
        key?.SetValue("ServerUrl", serverUrl, RegistryValueKind.String);
    }

    private static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);

    private static void RunSc(params string[] arguments) => RunNative(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments);

    private static void RunNative(string fileName, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException($"无法启动系统工具：{fileName}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"系统操作失败（{Path.GetFileName(fileName)}）：{stderr.Trim()} {stdout.Trim()}".Trim());
    }

    private static string ExtractApiError(string body, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
                return message.GetString() ?? fallback;
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("error", out var nestedError)
                && nestedError.TryGetProperty("message", out var nestedMessage))
                return nestedMessage.GetString() ?? fallback;
        }
        catch (JsonException)
        {
            // 使用下面的 HTTP 状态回退信息。
        }
        return fallback;
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
            // 临时下载目录清理失败不应覆盖安装结果；由系统临时目录回收。
        }
    }

    private static void DeleteDirectorySafely(string path, string allowedRoot)
    {
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("客户端卸载目录不在 BackupMonitor 专用目录内。");
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
