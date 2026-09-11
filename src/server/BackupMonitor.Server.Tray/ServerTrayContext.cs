using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;

namespace BackupMonitor.Server.Tray;

/// <summary>
/// 服务端托盘（整改清单 2026-09-10 · R6）。
///
/// 在此之前，全仓库只有一个 NotifyIcon，而且在 Agent.Tray 里——也就是说
/// **服务端死了，服务端这边没有任何界面看得见**。服务管理台是唯一的入口，
/// 而它是个要人主动去双击的窗口：关掉之后这台机器上就再没有东西会说话了。
///
/// 这个托盘要回答的是一个问题：这台服务器现在能不能收备份。
/// 因此判定不是「进程在不在」，而是三件事叠起来：
///   两个 Windows 服务的状态、/health（API 活着吗）、/health/db/local（库连得上吗）。
/// 图标三态对应的正是这三种处境：全绿 / 降级（服务在跑但库不通）/ 红（任一服务停了）。
/// </summary>
internal sealed class ServerTrayContext : ApplicationContext
{
    private const string ServerServiceName = "BackupMonitor.Server";
    private const string PostgreSqlServiceName = "BackupMonitor.PostgreSQL";
    private const string SetupExecutableName = "BackupMonitor.Server.Setup.exe";

    /// <summary>
    /// 10 秒。托盘不是监控系统——它只需要在人瞟一眼的时候是对的。
    /// 再密只是给本机 API 多加无谓的请求。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly string _installDirectory;
    private readonly int _apiPort;
    private readonly HttpClient _http;

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _detailItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly System.Windows.Forms.Timer _timer;

    private Icon? _currentIcon;
    private bool _refreshing;

    /// <summary>上一轮的健康档次。只在**转差**的那一次弹气泡，不刷屏。</summary>
    private HealthTier _lastTier = HealthTier.Unknown;

    public ServerTrayContext(string[] args)
    {
        _installDirectory = GetArgument(args, "--install-directory")
            ?? AppContext.BaseDirectory;
        _apiPort = ReadApiPort(_installDirectory);

        // 对端是本机自签名证书，而托盘不发送任何凭据、也不读取任何业务数据——
        // 它问的只是「这个端口上的服务还应不应答」。因此这里刻意不做指纹固定：
        // 固定就要先拿到期望指纹，而那个值只有 API 自己算得出来（由 pfx 推导），
        // 为了一个存活探测把整套引导逻辑搬进托盘不值得。
        // 代价被限制在回环地址上——非回环一律拒绝，见下面的回调。
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is not null
                && IPAddressIsLoopback(request.RequestUri.Host)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };

        _statusItem = new ToolStripMenuItem("状态：检查中…") { Enabled = false };
        _detailItem = new ToolStripMenuItem("　") { Enabled = false };
        _startItem = new ToolStripMenuItem("启动服务", null, async (_, _) => await ControlServicesAsync(start: true));
        _stopItem = new ToolStripMenuItem("停止服务", null, async (_, _) => await ControlServicesAsync(start: false));

        var menu = new ContextMenuStrip();
        // 版本号写在标题行上：报修的第一句话永远是「你那边是哪个版本」。
        menu.Items.Add(new ToolStripMenuItem($"BackupMonitor 服务端 {ReadServerVersion(_installDirectory)}") { Enabled = false });
        menu.Items.Add(_statusItem);
        menu.Items.Add(_detailItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开管理网页", null, (_, _) => OpenManagementSite()));
        menu.Items.Add(new ToolStripMenuItem("打开服务管理台", null, (_, _) => OpenSetupConsole()));
        menu.Items.Add(new ToolStripMenuItem("查看服务端指纹", null, async (_, _) => await ShowFingerprintAsync()));
        menu.Items.Add(new ToolStripMenuItem("立即刷新", null, async (_, _) => await RefreshAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        // 刻意不提供「退出并停止服务」：服务端停下来意味着全网客户端都传不上备份，
        // 那不该是托盘菜单里一个随手能点到的东西。要停就去服务管理台，那里有完整的后果说明。
        menu.Items.Add(new ToolStripMenuItem("退出托盘（服务继续运行）", null, (_, _) => ExitTrayOnly()));

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "BackupMonitor 服务端：检查中…",
            Icon = CreateStatusIcon(Color.DarkGray)
        };
        _currentIcon = _notifyIcon.Icon;
        _notifyIcon.DoubleClick += (_, _) => OpenManagementSite();

        _timer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>健康档次。顺序即严重程度，气泡只在数值变大（转差）时弹。</summary>
    private enum HealthTier
    {
        Healthy = 0,
        Degraded = 1,
        Down = 2,
        Unknown = 3
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            var server = ReadServiceStatus(ServerServiceName);
            var postgres = ReadServiceStatus(PostgreSqlServiceName);
            var apiOk = await ProbeAsync("/health");
            var database = await ProbeDatabaseAsync();

            var (tier, label, detail) = Judge(server, postgres, apiOk, database);
            var color = tier switch
            {
                HealthTier.Healthy => Color.SeaGreen,
                HealthTier.Degraded => Color.DarkOrange,
                HealthTier.Down => Color.Firebrick,
                _ => Color.DarkGray
            };

            _statusItem.Text = "状态：" + label;
            _detailItem.Text = detail;
            _notifyIcon.Text = Truncate($"BackupMonitor 服务端：{label}", 63);
            ReplaceIcon(CreateStatusIcon(color));

            // 服务状态确定时才收窄按钮；读不到就两个都留着——
            // 读不到状态不该把出路一起藏起来（与服务管理台、客户端托盘同一条原则）。
            var running = server is ServiceControllerStatus.Running;
            _startItem.Enabled = server is null || !running;
            _stopItem.Enabled = server is null || running;

            NotifyIfWorse(tier, label, detail);
        }
        catch (Exception ex)
        {
            _statusItem.Text = "状态：检查失败";
            _detailItem.Text = Truncate(ex.Message, 60);
            ReplaceIcon(CreateStatusIcon(Color.DarkGray));
            _startItem.Enabled = true;
            _stopItem.Enabled = true;
            _lastTier = HealthTier.Unknown;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// 三态判定。
    ///
    /// 「降级」这一档单独存在的理由：API 服务在跑、管理页面打得开，
    /// 而数据库自检不通——两个 Windows 服务这时都是「运行中」，
    /// 光看服务状态什么都看不出来，实际上所有备份和页面全废。这正是整改 R1
    /// 要堵的那个洞的另一面：库没了不会把 API 一起带走，所以必须有一处专门显示这件事。
    /// </summary>
    private (HealthTier Tier, string Label, string Detail) Judge(
        ServiceControllerStatus? server,
        ServiceControllerStatus? postgres,
        bool apiOk,
        (DatabaseProbe State, string? Reason) database)
    {
        if (server is null && postgres is null)
            return (HealthTier.Unknown, "读不到服务状态", "请确认服务端已安装，并以管理员身份运行托盘");

        var serverRunning = server is ServiceControllerStatus.Running;
        var postgresRunning = postgres is ServiceControllerStatus.Running;

        if (!serverRunning || !postgresRunning)
        {
            var stopped = !serverRunning && !postgresRunning
                ? "服务端与数据库都已停止"
                : !serverRunning ? "服务端服务已停止" : "数据库服务已停止";
            return (HealthTier.Down, "已停止", stopped + "，备份此刻收不进来");
        }

        if (!apiOk)
            return (HealthTier.Degraded, "服务在跑但网页不通", $"127.0.0.1:{_apiPort} 没有应答，管理网页可能打不开");

        switch (database.State)
        {
            case DatabaseProbe.Failed:
                return (HealthTier.Degraded, "服务在跑但数据库不通",
                    "两个服务都是运行中，但数据库自检失败——备份此刻写不进库，管理页面也打不开内容");

            case DatabaseProbe.Degraded:
                // 分区不齐不是「现在坏了」，是「到某一天会坏」：库连得上、备份照收，
                // 但缺的那一段时间一到，插入就会失败。橙灯而不是红灯，
                // 且话要说成待办事项——重启服务对它没有任何用处。
                return (HealthTier.Degraded, "数据库需要维护",
                    database.Reason ?? "数据库连接正常，但未来 30 天的分区不齐");

            case DatabaseProbe.Unknown:
                // 自检端点够不着（多半是服务端版本比托盘旧）。这时候既不能报「不通」
                // ——那是整改前那个永远亮着的假警报——也不能说三项全过。
                return (HealthTier.Healthy, "运行正常",
                    "服务与管理网页自检通过；数据库自检够不着，无法确认库是否可写");

            default:
                return (HealthTier.Healthy, "运行正常", "服务、数据库、管理网页三项自检均通过");
        }
    }

    /// <summary>转差才弹一次气泡；恢复和持平都不弹——刷屏的通知等于没有通知。</summary>
    private void NotifyIfWorse(HealthTier tier, string label, string detail)
    {
        if (tier > _lastTier && _lastTier != HealthTier.Unknown && tier != HealthTier.Unknown)
        {
            _notifyIcon.ShowBalloonTip(
                8000,
                "BackupMonitor 服务端：" + label,
                Truncate(detail, 240),
                tier == HealthTier.Down ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }

        _lastTier = tier;
    }

    private async Task<bool> ProbeAsync(string path)
    {
        try
        {
            using var response = await _http.GetAsync($"https://127.0.0.1:{_apiPort}{path}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>数据库自检的四种结局。<see cref="Unknown"/> 是「问不出来」，不是「坏了」。</summary>
    private enum DatabaseProbe
    {
        Ok,
        Degraded,
        Failed,
        Unknown
    }

    /// <summary>
    /// 数据库自检。走 /health/db/local——只认回环、只回一个档次词的那个端点。
    ///
    /// **不要改回 /health/db**：那个端点要 system.manage 令牌，托盘手里没有令牌，
    /// 拿回来的永远是 401。按「非 2xx 即数据库不通」去判，结果就是每一台健康的机器上
    /// 都常亮橙灯——报警一旦永远为真，它就不再是报警。
    /// </summary>
    private async Task<(DatabaseProbe State, string? Reason)> ProbeDatabaseAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"https://127.0.0.1:{_apiPort}/health/db/local");
            if (!response.IsSuccessStatusCode)
            {
                // 404 = 服务端还没有这个端点（版本比托盘旧）或对端不是回环；
                // 401/403 = 端点被改回要授权了。三种都属于「问不出来」。
                return (DatabaseProbe.Unknown, null);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var reason = root.TryGetProperty("reason", out var reasonValue)
                && reasonValue.ValueKind == JsonValueKind.String
                    ? reasonValue.GetString()
                    : null;

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return (DatabaseProbe.Ok, null);

            var status = root.TryGetProperty("status", out var statusValue)
                && statusValue.ValueKind == JsonValueKind.String
                    ? statusValue.GetString()
                    : null;

            return string.Equals(status, "Degraded", StringComparison.OrdinalIgnoreCase)
                ? (DatabaseProbe.Degraded, reason)
                : (DatabaseProbe.Failed, reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 连不上 API 这件事由 /health 那一探负责判定并显示；
            // 这里再喊一次「数据库不通」只会盖住真正的原因。
            return (DatabaseProbe.Unknown, null);
        }
    }

    /// <summary>服务不存在（未安装）与读不到权限都返回 null，交给判定去区分处置。</summary>
    private static ServiceControllerStatus? ReadServiceStatus(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            service.Refresh();
            return service.Status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private async Task ControlServicesAsync(bool start)
    {
        if (!start)
        {
            var answer = MessageBox.Show(
                "停止服务端会让全网客户端都传不上备份，管理网页也会打不开。" + Environment.NewLine + Environment.NewLine
                + "确定要停止吗？",
                "停止 BackupMonitor 服务端",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
        }

        _startItem.Enabled = false;
        _stopItem.Enabled = false;
        try
        {
            // 托盘跑在登录用户身份下，通常没有操作服务的权限，因此一律走提权的 sc.exe——
            // 与客户端托盘的兜底路径一致。
            // 顺序：起的时候先库后 API，停的时候先 API 后库。反过来会让 API 在库消失之后
            // 继续跑一会儿，日志里刷一片连接失败，看起来像是出了故障。
            var order = start
                ? new[] { PostgreSqlServiceName, ServerServiceName }
                : new[] { ServerServiceName, PostgreSqlServiceName };
            foreach (var service in order)
                await Task.Run(() => RunElevatedServiceControl(start ? "start" : "stop", service));
        }
        finally
        {
            await RefreshAsync();
        }
    }

    private static bool RunElevatedServiceControl(string verb, string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = $"{verb} \"{serviceName}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process?.WaitForExit(30000);
            return process?.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private void OpenManagementSite() =>
        OpenExternal($"https://{Environment.MachineName}:{_apiPort}/", "无法打开管理网页");

    private void OpenSetupConsole()
    {
        var path = Path.Combine(_installDirectory, SetupExecutableName);
        if (!File.Exists(path))
        {
            MessageBox.Show(
                $"安装目录中找不到服务管理台程序：{path}" + Environment.NewLine
                + "请用服务端安装器执行一次「修复 / 升级安装」。",
                "BackupMonitor 服务端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 管理台的清单要求管理员身份，UseShellExecute 让 UAC 正常弹出来。
        OpenExternal(path, "无法打开服务管理台");
    }

    /// <summary>
    /// 服务端 TLS 证书指纹，供操作员带外核对——客户端安装器会显示同一个值，
    /// 由人比对两边是否一致，这是识破中间人的唯一手段（自签名证书没有公共 CA 背书）。
    /// 取自匿名端点 /api/v1/public/server-identity：指纹在每一次 TLS 握手里本来就公开出示，
    /// 不是机密，托盘因此不必碰 pfx 和口令。
    /// </summary>
    private async Task ShowFingerprintAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"https://127.0.0.1:{_apiPort}/api/v1/public/server-identity");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var fingerprint = document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("fingerprint", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                MessageBox.Show(
                    "当前部署形态下服务端进程手里没有 TLS 证书（TLS 由 nginx 终结），因此没有指纹可显示。",
                    "服务端指纹", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 指纹是拿来逐段念给对面听的，直接放进剪贴板省掉手抄。
            try
            {
                Clipboard.SetText(fingerprint);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
            {
                // 剪贴板被别的进程占着是常事，不影响把值显示出来。
            }

            MessageBox.Show(
                fingerprint + Environment.NewLine + Environment.NewLine
                + "（已复制到剪贴板）客户端安装器上显示的指纹必须与这一串完全一致。",
                "服务端指纹", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "无法读取服务端指纹：" + ex.Message + Environment.NewLine
                + "服务端可能没有运行。",
                "服务端指纹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenExternal(string target, string failureTitle)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, failureTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExitTrayOnly()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _http.Dispose();
        Application.ExitThread();
    }

    /// <summary>
    /// 已安装服务端的版本。取的是 BackupMonitor.Api.dll——那是真正在跑的那个程序；
    /// 托盘自己的版本在升级之后可能还停在旧的（它不随服务重启）。
    /// </summary>
    private static string ReadServerVersion(string installDirectory)
    {
        try
        {
            var path = Path.Combine(installDirectory, "BackupMonitor.Api.dll");
            if (!File.Exists(path))
                return "版本未知";

            var info = FileVersionInfo.GetVersionInfo(path);
            var version = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? "版本未知" : "v" + version.Split('+')[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "版本未知";
        }
    }

    /// <summary>与 ServerMaintenance.GetInstalledApiPort 同一口径，读同一个文件。</summary>
    private static int ReadApiPort(string installDirectory)
    {
        try
        {
            var path = Path.Combine(installDirectory, "appsettings.Turnkey.json");
            if (!File.Exists(path))
                return 5080;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
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

    private static bool IPAddressIsLoopback(string host) =>
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

    private static string? GetArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 1)] + "…";

    private void ReplaceIcon(Icon icon)
    {
        var previous = _currentIcon;
        _currentIcon = icon;
        _notifyIcon.Icon = icon;
        previous?.Dispose();
    }

    private static Icon CreateStatusIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        using (var border = new Pen(Color.White, 1))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillEllipse(brush, 2, 2, 12, 12);
            graphics.DrawEllipse(border, 2, 2, 12, 12);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
