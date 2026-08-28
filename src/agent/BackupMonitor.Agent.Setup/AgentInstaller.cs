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
    string? ServerCertificatePem,
    string? ServerInstanceId);

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
        string? presentedFingerprint = null;
        using var client = CreateHttpClient(onServerCertificate: value => presentedFingerprint = value);
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
        var serverInstanceId = data.TryGetProperty("serverInstanceId", out var instance)
            ? instance.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(publicKey))
            throw new InvalidOperationException("服务端没有提供 Agent 指令签名公钥，请检查服务端密钥配置。");

        if (string.Equals(deploymentMode, "LanSimple", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(certificateFingerprint))
            throw new InvalidOperationException("服务端没有提供 TLS 证书指纹，请检查 Turnkey 服务端证书配置。");

        // 把要固定的指纹和实际通话的那张证书绑死。两者不一致说明响应体里的指纹
        // 不是这条连接对端的证书——继续下去会把客户端配置成固定一张它从未验证过的证书。
        if (!string.IsNullOrWhiteSpace(certificateFingerprint)
            && presentedFingerprint is not null
            && !string.Equals(
                NormalizeFingerprint(certificateFingerprint),
                presentedFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "服务端返回的 TLS 证书指纹与本次连接实际出示的证书不一致，已中止安装。"
                + Environment.NewLine
                + $"响应体：{NormalizeFingerprint(certificateFingerprint)}"
                + Environment.NewLine
                + $"实际连接：{presentedFingerprint}");
        }

        ValidatePublicKey(publicKey);
        return new AgentBootstrap(
            publicKey,
            string.IsNullOrWhiteSpace(packagePath) ? "/downloads/BackupMonitor.Agent.zip" : packagePath,
            automaticEnrollment,
            deploymentMode,
            certificateFingerprint,
            certificatePem,
            serverInstanceId);
    }

    public async Task InstallAsync(
        string serverUrl,
        string displayName,
        string registrationToken,
        string? expectedServerFingerprint,
        string? discoveredServerInstanceId,
        Func<string, Task<bool>> confirmServerFingerprintAsync,
        IProgress<InstallProgress> progress,
        CancellationToken ct)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        progress.Report(new InstallProgress("正在读取服务端安装配置…", 5));
        var bootstrap = await GetBootstrapAsync(baseUrl, ct);

        // 带外核对必须挡在下载之前：这一步之后的每一个字节都来自这条连接，
        // 包括 Agent 程序包本身。等装完再问就没有意义了。
        progress.Report(new InstallProgress("等待核对服务端证书指纹…", 8));
        await VerifyServerFingerprintAsync(
            bootstrap.ServerCertificateFingerprint,
            expectedServerFingerprint,
            confirmServerFingerprintAsync);

        // 局域网发现应答自报的实例 ID，与这条已核对过指纹的 HTTPS 连接自报的实例 ID，
        // 必须是同一个。对不上意味着「用户在列表里选的那台」和「真正应答的那台」
        // 不是一台机器——发现协议是无认证的明文 UDP，这正是伪造应答该有的样子。
        // 装下去的后果是客户端把一个错误的实例 ID 当成日后地址重发现的锚点。
        if (!string.IsNullOrWhiteSpace(discoveredServerInstanceId)
            && !string.IsNullOrWhiteSpace(bootstrap.ServerInstanceId)
            && !string.Equals(discoveredServerInstanceId, bootstrap.ServerInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "局域网发现到的服务端实例 ID 与该地址实际应答的实例 ID 不一致，已中止安装。"
                + Environment.NewLine
                + $"发现应答：{discoveredServerInstanceId}"
                + Environment.NewLine
                + $"实际应答：{bootstrap.ServerInstanceId}"
                + Environment.NewLine
                + "请改为手工填写服务端地址后重试。");
        }

        if (!bootstrap.AutomaticEnrollment && string.IsNullOrWhiteSpace(registrationToken))
            throw new InvalidOperationException("当前服务端是 Secure 模式，请在“高级选项”中填写注册令牌。");
        var packageUri = ResolvePackageUri(baseUrl, bootstrap.AgentPackagePath);

        var tempRoot = Path.Combine(Path.GetTempPath(), "BackupMonitor.Agent.Setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var packagePath = Path.Combine(tempRoot, "BackupMonitor.Agent.zip");
            progress.Report(new InstallProgress("正在下载客户端组件…", 10));
            await DownloadPackageAsync(
                packageUri, packagePath, bootstrap.ServerCertificateFingerprint, progress, ct);

            var payloadRoot = Path.Combine(tempRoot, "payload");
            progress.Report(new InstallProgress("正在解压客户端组件…", 62));
            ZipFile.ExtractToDirectory(packagePath, payloadRoot, overwriteFiles: true);
            ValidatePayload(payloadRoot);

            await Task.Run(() =>
            {
                progress.Report(new InstallProgress("正在停止旧版本服务和托盘…", 68));
                // 托盘和服务是两个进程，且托盘就跑在安装目录里。只停服务的话，
                // 托盘仍然锁着 Accessibility.dll、System.Windows.Forms.dll 这些
                // WinForms 程序集，紧接着的覆盖复制必然撞共享冲突——
                // 「修复/重新安装」在任何一台托盘正常运行的机器上都会失败。
                StopTray();
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

                // 优先用 HTTPS 引导接口返回的实例 ID：它来自一条指纹已被核对过的连接，
                // 而发现应答是任何人都能广播的明文 UDP。只有旧版本服务端不返回它时
                // 才退回发现到的值——那时至少还有 TLS 指纹这一道锚点。
                WriteInitialAgentState(dataDir, bootstrap.ServerInstanceId ?? discoveredServerInstanceId);

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

            // 两个目录各删各的、各自收集错误。安装目录删不掉就直接抛的话，
            // 数据目录会原封不动留在盘上——里面的 state.json 存着客户端身份和证书，
            // 重装后被沿用，表现成「客户端重装了但服务端看到的还是旧身份」。
            var failures = new List<Exception>();
            TryDelete(() => DeleteDirectorySafely(Expand(InstallDirectory), Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BackupMonitor")));
            if (removeData)
                TryDelete(() => DeleteDirectorySafely(Expand(DataDirectory), Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMonitor")));

            if (failures.Count > 0)
                throw new AggregateException(
                    "卸载未能删除全部目录，残留内容会在下次安装时被沿用，请重启计算机后再次运行卸载。",
                    failures);

            void TryDelete(Action delete)
            {
                try
                {
                    delete();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(ex);
                }
            }
        }, ct);
    }

    /// <summary>
    /// 一键交付的服务端用的是安装时自签的证书，客户端机器不可能预先信任它，
    /// 默认证书校验一定失败——而 /api/v1/agent/bootstrap 的用途恰恰就是把证书指纹
    /// 发给客户端做后续固定校验。所以引导那一次请求按设计只能是首次信任（TOFU）：
    /// 跳过链校验，但记下对端实际出示的证书指纹，回来和响应体里的指纹对账。
    ///
    /// <paramref name="pinnedFingerprint"/> 有值时改为严格固定校验，只认这一张证书，
    /// 与运行期 AgentApiClient 的判定口径一致。引导之后的所有请求都走这条路径。
    /// </summary>
    private static HttpClient CreateHttpClient(
        string? pinnedFingerprint = null,
        Action<string>? onServerCertificate = null)
    {
        var handler = new HttpClientHandler();

        if (!string.IsNullOrWhiteSpace(pinnedFingerprint))
        {
            var expected = NormalizeFingerprint(pinnedFingerprint);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && string.Equals(
                    NormalizeFingerprint(Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))),
                    expected,
                    StringComparison.OrdinalIgnoreCase);
        }
        else if (onServerCertificate is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                    return false;
                onServerCertificate(
                    NormalizeFingerprint(Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))));
                return true;
            };
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    /// <summary>
    /// 带外核对服务端 TLS 指纹。
    ///
    /// 自签名证书没有公共 CA 背书，GetBootstrapAsync 里那道「响应体指纹 == 实际出示证书」
    /// 的对账只能发现服务端自身配置错乱：中间人出示自己的证书、响应体里填自己的指纹，
    /// 两者一样自洽。要识破它，只能由人拿服务端安装器上显示的值来比。
    ///
    /// 填了期望值就精确比对，不再打扰操作员——这条路既省事又比人眼更严格，
    /// 批量装机时粘贴同一个值即可。留空才回落到人工确认。
    /// </summary>
    private static async Task VerifyServerFingerprintAsync(
        string? actualFingerprint,
        string? expectedFingerprint,
        Func<string, Task<bool>> confirmAsync)
    {
        var actual = CertificateFingerprint.Normalize(actualFingerprint);
        if (actual.Length == 0)
        {
            throw new InvalidOperationException(
                "服务端没有提供 TLS 证书指纹，无法完成带外核对，已中止安装。");
        }

        if (!string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            if (!CertificateFingerprint.Matches(expectedFingerprint, actual))
            {
                throw new InvalidOperationException(
                    "服务端 TLS 证书指纹与你填写的期望值不一致，已中止安装。"
                    + Environment.NewLine + Environment.NewLine
                    + "你填写的：" + Environment.NewLine
                    + CertificateFingerprint.ToDisplayBlock(expectedFingerprint)
                    + Environment.NewLine + Environment.NewLine
                    + "实际连到的：" + Environment.NewLine
                    + CertificateFingerprint.ToDisplayBlock(actual)
                    + Environment.NewLine + Environment.NewLine
                    + "这说明连接对端不是你预期的那台服务端。请勿继续，联系管理员核实。");
            }

            return;
        }

        if (!await confirmAsync(actual))
        {
            throw new InvalidOperationException(
                "未确认服务端证书指纹，已取消安装。"
                + Environment.NewLine
                + "请在服务端安装器上点「查看服务端指纹」取得正确取值后重试。");
        }
    }

    private static string NormalizeFingerprint(string value) =>
        value.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

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
        string? serverCertificateFingerprint,
        IProgress<InstallProgress> progress,
        CancellationToken ct)
    {
        // 引导已经拿到指纹，从这一步起一律严格固定校验，不再放行任何其他证书。
        using var client = CreateHttpClient(serverCertificateFingerprint);
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
            try
            {
                File.Copy(sourcePath, Path.Combine(installDir, name), overwrite: true);
            }
            catch (IOException ex)
            {
                // 原样抛出的是一句英文的 "being used by another process"，只说了文件名，
                // 没说该拿它怎么办。这一步能撞占用的就那么几种情况，直接写出来。
                throw new IOException(
                    $"覆盖 {name} 失败：文件正被其他进程占用。"
                    + Environment.NewLine
                    + "请确认托盘图标已退出、Agent 服务已停止（可用下方「停止服务」），再重试；"
                    + "仍然失败通常是杀毒软件正在扫描该目录，稍等片刻或重启后再装。",
                    ex);
            }
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

    /// <summary>
    /// 把服务端实例 ID 预写进 state.json（方案 C 的前置）。
    ///
    /// Agent 自己在首次心跳成功后也会问一次并记下来，这里再写一遍是为了让锚点
    /// 从第一次连接之前就存在：安装这一刻的连接是用户带外核对过指纹的，
    /// 可信度高于运行期任何一次自动获取。
    ///
    /// 必须在既有 state.json 上做合并而不是覆盖：「修复安装」时那个文件里
    /// 存着本机的客户端身份、私钥和证书，整份重写等于把这台机器变成一台新客户端，
    /// 服务端上会多出一条僵尸记录。
    /// </summary>
    private static void WriteInitialAgentState(string dataDir, string? serverInstanceId)
    {
        if (string.IsNullOrWhiteSpace(serverInstanceId))
            return;

        var statePath = Path.Combine(dataDir, "state.json");
        JsonObject state;
        try
        {
            state = File.Exists(statePath)
                ? JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            // 读不出来的状态文件按「没有」处理，与 Agent 自身的 LoadState 一致。
            state = new JsonObject();
        }

        state["serverInstanceId"] = serverInstanceId;

        // 上一次运行期重发现出来的地址不能留：它指向的是搬家前后的某台机器，
        // 而这次安装用户明确选了一个地址（已写进 appsettings.json）。
        // 留着的话 Agent 启动后仍会优先用那个旧地址，表现成「装完还是连不上」。
        state.Remove("serverUrlOverride");

        File.WriteAllText(
            statePath,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        SecureFileSystem.ApplyFileAcl(statePath);
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

    /// <summary>
    /// 停止 Agent 服务（不删除）。
    ///
    /// 停止 Agent 意味着这台机器上的备份从此刻起无人看管——扫描、预检、上传全停，
    /// 服务端会把它记成离线。所以这是个需要人明确确认的动作，但它必须存在：
    /// 现场要做磁盘维护、要临时腾出带宽、要排查是不是 Agent 影响了业务软件，
    /// 没有这个按钮，唯一的出路是 services.msc，而那不是给运维之外的人准备的地方。
    /// </summary>
    public void StopService()
    {
        using var service = new ServiceController(ServiceName);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped)
            return;

        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    /// <summary>启动 Agent 服务。</summary>
    public void StartService() => StartServiceAndWait();

    /// <summary>当前服务状态，给维护界面显示。</summary>
    public static string GetServiceStatusText()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            return service.Status switch
            {
                ServiceControllerStatus.Running => "运行中",
                ServiceControllerStatus.Stopped => "已停止",
                ServiceControllerStatus.StartPending => "正在启动",
                ServiceControllerStatus.StopPending => "正在停止",
                ServiceControllerStatus.Paused => "已暂停",
                _ => service.Status.ToString()
            };
        }
        catch (InvalidOperationException)
        {
            return "未安装";
        }
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
                {
                    process.Kill(entireProcessTree: true);
                    // Kill 只是投递终止请求，句柄要等进程真正退出才释放；
                    // 不等这一下，紧接着删安装目录就会撞上托盘还占着的文件。
                    process.WaitForExit(5000);
                }
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
        // 句柄释放是异步的：服务刚停、托盘刚退，文件可能还锁着几百毫秒。
        // 重试比一次就失败更贴合实际，全部用尽再抛出可读的错误。
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt >= 9)
                    throw new IOException(
                        $"删除 {path} 失败：{ex.Message}。该文件仍被其他进程占用，请重启计算机后再次运行卸载。",
                        ex);

                Thread.Sleep(1000);
            }
        }
    }
}
