using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Agent.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string DefaultServiceName = "BackupMonitor Agent";
    private const string DefaultRunKey = "BackupMonitor.Agent.Tray";

    private readonly string _serviceName;
    private readonly string _serverUrl;
    private readonly string _dataDirectory;
    private readonly string _shownNotificationPath;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _lastBackupItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _startServiceItem;
    private readonly ToolStripMenuItem _stopServiceItem;
    private readonly System.Windows.Forms.Timer _timer;
    private Icon? _currentIcon;
    private bool _refreshing;

    public TrayApplicationContext(string[] args)
    {
        _serviceName = GetArgument(args, "--service-name") ?? DefaultServiceName;
        _serverUrl = GetArgument(args, "--server-url") ?? LoadServerUrl();
        _dataDirectory = ResolveDataDirectory(GetArgument(args, "--data-directory"));
        _shownNotificationPath = Path.Combine(_dataDirectory, "tray-notifications-shown.json");

        _statusItem = new ToolStripMenuItem("状态：检查中…")
        {
            Enabled = false
        };
        // 「服务在跑」回答不了「它到底有没有在备份」。这一项写的是客户端这一侧
        // 能知道的最晚那个点：上一次把备份传完并交给服务端校验的时刻。
        _lastBackupItem = new ToolStripMenuItem("上次成功备份：读取中…") { Enabled = false };
        _refreshItem = new ToolStripMenuItem("刷新状态", null, async (_, _) => await RefreshStatusAsync());

        // 托盘此前只能"停止服务并退出"。停完之后托盘也没了，想再把服务起回来
        // 只剩 services.msc 这一条路——而托盘的使用者恰恰是最不该被送去那里的人。
        // 这两项按当前状态互斥显示：停着的时候只给"启动"，跑着的时候只给"停止"。
        _startServiceItem = new ToolStripMenuItem("启动 Agent 服务", null, async (_, _) => await ToggleServiceAsync(start: true));
        _stopServiceItem = new ToolStripMenuItem("停止 Agent 服务（托盘保留）", null, async (_, _) => await ToggleServiceAsync(start: false));

        var menu = new ContextMenuStrip();
        // 版本号写在标题行上。报修时第一句话永远是「你那边是哪个版本」，
        // 而此前这台机器上没有任何地方能回答——要么去属性页看 exe，要么去服务端的客户端列表里找自己。
        menu.Items.Add(new ToolStripMenuItem($"BackupMonitor Agent {ReadAgentVersion()}") { Enabled = false });
        menu.Items.Add(_statusItem);
        menu.Items.Add(_lastBackupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开服务端客户端页", null, (_, _) => OpenServerPage()));
        menu.Items.Add(_refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startServiceItem);
        menu.Items.Add(_stopServiceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("正常退出并停止 Agent 服务", null, async (_, _) => await StopServiceAndExitAsync()));
        menu.Items.Add(new ToolStripMenuItem("退出托盘（服务继续运行）", null, (_, _) => ExitTrayOnly()));

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "BackupMonitor Agent：检查中…",
            Icon = CreateStatusIcon(Color.DarkGray)
        };
        _currentIcon = _notifyIcon.Icon;
        _notifyIcon.DoubleClick += (_, _) => OpenServerPage();

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) =>
        {
            await RefreshStatusAsync();
            ShowNewNotifications();
        };
        _timer.Start();
        _ = RefreshStatusAsync();
        ShowNewNotifications();
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            var status = await Task.Run(GetServiceStatus);
            var state = await Task.Run(ReadAgentState);
            var (label, color) = Judge(status, state);

            _statusItem.Text = $"状态：{label}";
            _lastBackupItem.Text = "上次成功备份：" + DescribeLastBackup(state);
            _notifyIcon.Text = TruncateTooltip($"BackupMonitor Agent：{label}");
            ReplaceIcon(CreateStatusIcon(color));
            _refreshItem.Enabled = true;
            UpdateServiceMenuItems(status);
        }
        catch (Exception ex)
        {
            _statusItem.Text = "状态：无法读取服务";
            _lastBackupItem.Text = "上次成功备份：读不到";
            _notifyIcon.Text = TruncateTooltip($"BackupMonitor Agent：无法读取服务（{ex.Message}）");
            ReplaceIcon(CreateStatusIcon(Color.DarkGray));
            _refreshItem.Enabled = true;
            // 读不到状态时两个都留着：让人还能试一把，好过把出路一起藏起来。
            _startServiceItem.Visible = true;
            _stopServiceItem.Visible = true;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// 绿灯判据（R7）：**服务在跑 且 最近一次心跳成功 且 无 LastError**。
    ///
    /// 改之前只读 ServiceController 一个来源，于是「Agent 服务正常跑着，
    /// 但连不上服务端 / 证书被拒 / 一直登记失败 / 一个任务都没配」时，
    /// 托盘照样是绿色的「运行中」。服务在跑 ≠ 备份在做，而托盘是这台机器上
    /// 唯一有人会看的界面——它说绿，人就不会再去看第二眼。
    ///
    /// 中间态一律用橙色并在 tooltip 里写明原因：橙色说的是「它活着，但有件事没成」，
    /// 这与红色（服务根本没跑）是完全不同的两种处置。
    ///
    /// 读不到 state.json 时**退回只按服务状态判**，不降级成橙色：
    /// 刚装完还没跑起来、文件正被原子替换的那一瞬间都会读不到，
    /// 拿这个报警等于每天造一批假警报。与 catch 分支同一条原则。
    /// </summary>
    private (string Label, Color Color) Judge(ServiceControllerStatus status, AgentStateView? state)
    {
        var serviceRunning = status == ServiceControllerStatus.Running;
        if (!serviceRunning)
        {
            return status switch
            {
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ("启动中", Color.DarkOrange),
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ("停止中", Color.DarkOrange),
                ServiceControllerStatus.Stopped => ("已停止", Color.Firebrick),
                _ => ("状态未知", Color.DarkGray)
            };
        }

        if (state is null)
            return ("运行中", Color.SeaGreen);

        if (!string.IsNullOrWhiteSpace(state.LastError))
            return ($"运行中，但有错误：{Shorten(state.LastError!, 40)}", Color.DarkOrange);

        if (state.ClientId is null)
            return ("运行中，尚未完成登记（可能在等待管理员审批）", Color.DarkOrange);

        if (state.LastHeartbeatAtUtc is null)
            return ("运行中，还没有成功连上服务端", Color.DarkOrange);

        // 心跳间隔可配（5~300 秒）。托盘不知道当前配置值，用一个宽的固定判据：
        // 15 分钟没有一次成功心跳，无论配成多久都已经是「联系不上了」。
        // 判紧了会在正常的长间隔配置下天天误报，而误报几次之后这个颜色就没人信了。
        var silence = DateTime.UtcNow - state.LastHeartbeatAtUtc.Value;
        if (silence > TimeSpan.FromMinutes(15))
            return ($"运行中，但已 {DescribeAge(state.LastHeartbeatAtUtc.Value)}没连上服务端", Color.DarkOrange);

        return ("运行中", Color.SeaGreen);
    }

    private static string DescribeLastBackup(AgentStateView? state)
    {
        if (state is null)
            return "读不到";
        if (state.LastSuccessfulUploadAtUtc is null)
        {
            // 「还没有过」这四个字在刚升级完的机器上是会骗人的：这个字段是后加的，
            // 老版本从不写它，于是一台今天早晨刚备份成功的机器升上来之后也显示「还没有过」。
            // 把记录起点一并说出来，人就能自己判断这是「真没备份过」还是「升级前的没记下来」。
            return state.UploadTrackingSinceUtc is { } since
                ? $"还没有过（自 {since.ToLocalTime():MM-dd HH:mm} 起才开始记录）"
                : "还没有过";
        }
        return $"{state.LastSuccessfulUploadAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}（{DescribeAge(state.LastSuccessfulUploadAtUtc.Value)}前）";
    }

    private static string DescribeAge(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero)
            return "刚刚";
        if (span < TimeSpan.FromMinutes(1))
            return $"{(int)span.TotalSeconds} 秒";
        if (span < TimeSpan.FromHours(1))
            return $"{(int)span.TotalMinutes} 分钟";
        if (span < TimeSpan.FromDays(1))
            return $"{(int)span.TotalHours} 小时";
        return $"{(int)span.TotalDays} 天";
    }

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>
    /// 只取托盘用得着的那几个字段。刻意**不**引用 Agent 工程的 AgentState：
    /// 那个类型带着候选清单、指令 nonce、扫描排期一大摞运行期状态，
    /// 托盘为了显示三个字段没必要把整个 Agent 工程拖进来，
    /// 也没必要在 Agent 每加一个状态字段时跟着重新编译。
    /// </summary>
    private sealed class AgentStateView
    {
        public Guid? ClientId { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastHeartbeatAtUtc { get; set; }
        public DateTime? LastSuccessfulUploadAtUtc { get; set; }
        public DateTime? UploadTrackingSinceUtc { get; set; }
    }

    /// <summary>
    /// 读 Agent 数据目录下的 state.json。
    ///
    /// 任何读不出来的情形都返回 null（文件不存在、正在被原子替换、JSON 半截、没权限）——
    /// 调用方会退回「只按服务状态判」。托盘绝不能因为读文件失败而把自己搞崩：
    /// 它是这台机器上唯一有人会看的界面。
    /// </summary>
    private AgentStateView? ReadAgentState()
    {
        try
        {
            var path = Path.Combine(_dataDirectory, "state.json");
            if (!File.Exists(path))
                return null;

            // Agent 用「写临时文件 + File.Move」原子替换 state.json，
            // 但读的一侧仍然可能撞上正在被替换的句柄，因此要允许共享读写。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<AgentStateView>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private ServiceControllerStatus GetServiceStatus()
    {
        using var service = new ServiceController(_serviceName);
        service.Refresh();
        return service.Status;
    }

    private void ShowNewNotifications()
    {
        try
        {
            var queuePath = Path.Combine(_dataDirectory, "tray-notifications.json");
            if (!File.Exists(queuePath))
                return;

            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            };
            var rows = JsonSerializer.Deserialize<List<AgentNotificationDto>>(
                           File.ReadAllText(queuePath), jsonOptions)
                       ?? [];
            var shown = File.Exists(_shownNotificationPath)
                ? JsonSerializer.Deserialize<HashSet<Guid>>(
                      File.ReadAllText(_shownNotificationPath), jsonOptions) ?? []
                : [];

            var pending = rows
                .Where(n => n.Id != Guid.Empty && !shown.Contains(n.Id))
                .OrderBy(n => n.CreatedAt)
                .ThenBy(n => n.Id)
                .Take(3)
                .ToList();
            if (pending.Count == 0)
                return;

            foreach (var notification in pending)
            {
                _notifyIcon.ShowBalloonTip(
                    7000,
                    TruncateBalloon(notification.Title, 64),
                    TruncateBalloon(notification.Message ?? "BackupMonitor Agent", 240),
                    ToToolTipIcon(notification.Severity));
                shown.Add(notification.Id);
            }

            SaveShownNotificationIds(shown);
        }
        catch (Exception ex)
        {
            // 托盘提示失败不影响 Agent 服务和心跳采集。
            Debug.WriteLine($"BackupMonitor tray notification error: {ex.Message}");
        }
    }

    private void SaveShownNotificationIds(IEnumerable<Guid> ids)
    {
        Directory.CreateDirectory(_dataDirectory);
        var tempPath = _shownNotificationPath + ".tmp";
        var recent = ids.TakeLast(500).ToArray();
        File.WriteAllText(tempPath, JsonSerializer.Serialize(recent));
        File.Move(tempPath, _shownNotificationPath, true);
    }

    private static ToolTipIcon ToToolTipIcon(string? severity) =>
        severity?.ToLowerInvariant() switch
        {
            "critical" => ToolTipIcon.Error,
            "warning" => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info
        };

    private void OpenServerPage()
    {
        var url = _serverUrl.TrimEnd('/') + "/#/clients";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开服务端页面：{ex.Message}", "BackupMonitor Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task StopServiceAndExitAsync()
    {
        var answer = MessageBox.Show(
            "这会正常停止 Agent 服务，停止后不会继续采集和上传。确定退出吗？",
            "正常退出 BackupMonitor Agent",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _refreshItem.Enabled = false;
        var exitRequested = false;
        try
        {
            await Task.Run(StopService);
            exitRequested = true;
            ExitTrayOnly();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (TryElevatedStop())
            {
                exitRequested = true;
                ExitTrayOnly();
            }
            else
                MessageBox.Show("没有停止 Agent 服务的权限。请使用管理员身份操作。", "BackupMonitor Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Agent 服务未能正常停止：{ex.Message}", "BackupMonitor Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!exitRequested)
                _refreshItem.Enabled = true;
        }
    }

    /// <summary>
    /// 启停服务但不退出托盘。
    ///
    /// 托盘跑在登录用户身份下，通常没有操作服务的权限，所以拿不到权限时
    /// 走一次 UAC 提权的 sc.exe——跟"停止并退出"用的是同一条兜底路径。
    /// </summary>
    private async Task ToggleServiceAsync(bool start)
    {
        if (!start)
        {
            var answer = MessageBox.Show(
                "停止后这台机器将不再扫描、预检和上传备份，服务端会把它记为离线。托盘会继续保留，可以随时再启动。"
                + Environment.NewLine + Environment.NewLine
                + "确定要停止 Agent 服务吗？",
                "停止 Agent 服务",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
        }

        _startServiceItem.Enabled = false;
        _stopServiceItem.Enabled = false;
        try
        {
            await Task.Run(() => { if (start) StartService(); else StopService(); });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!TryElevatedServiceControl(start ? "start" : "stop"))
            {
                MessageBox.Show(
                    $"没有{(start ? "启动" : "停止")} Agent 服务的权限。请使用管理员身份操作。",
                    "BackupMonitor Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Agent 服务未能{(start ? "启动" : "停止")}：{ex.Message}",
                "BackupMonitor Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _startServiceItem.Enabled = true;
            _stopServiceItem.Enabled = true;
            await RefreshStatusAsync();
        }
    }

    /// <summary>按当前状态决定给哪个入口：停着只给"启动"，跑着只给"停止"。</summary>
    private void UpdateServiceMenuItems(ServiceControllerStatus status)
    {
        var running = status is ServiceControllerStatus.Running
            or ServiceControllerStatus.StartPending
            or ServiceControllerStatus.ContinuePending;
        _startServiceItem.Visible = !running;
        _stopServiceItem.Visible = running;
    }

    private void StopService()
    {
        using var service = new ServiceController(_serviceName);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped)
            return;

        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    private void StartService()
    {
        using var service = new ServiceController(_serviceName);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running)
            return;

        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    private bool TryElevatedStop() => TryElevatedServiceControl("stop");

    private bool TryElevatedServiceControl(string verb)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = $"{verb} \"{_serviceName.Replace("\"", string.Empty)}\"",
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

    private void ExitTrayOnly()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        Application.ExitThread();
    }

    private void ReplaceIcon(Icon icon)
    {
        var previous = _currentIcon;
        _currentIcon = icon;
        _notifyIcon.Icon = icon;
        previous?.Dispose();
    }

    private string LoadServerUrl()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
                return "http://127.0.0.1:5080";

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Agent", out var agent)
                && agent.TryGetProperty("ServerUrl", out var serverUrl)
                && serverUrl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(serverUrl.GetString())
                ? serverUrl.GetString()!
                : "http://127.0.0.1:5080";
        }
        catch
        {
            return "http://127.0.0.1:5080";
        }
    }

    private string ResolveDataDirectory(string? argument)
    {
        var configured = argument;
        if (string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.TryGetProperty("Agent", out var agent)
                        && agent.TryGetProperty("DataDirectory", out var dataDirectory)
                        && dataDirectory.ValueKind == JsonValueKind.String)
                        configured = dataDirectory.GetString();
                }
            }
            catch
            {
                // Use the common data directory fallback below.
            }
        }

        configured = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMonitor", "Agent")
            : Environment.ExpandEnvironmentVariables(configured);
        return Path.GetFullPath(configured);
    }

    /// <summary>
    /// 客户端版本号。取的是同目录下 Agent 服务主程序的版本，而不是托盘自己的——
    /// 人问「这台机器是哪个版本」时，指的是正在跑备份的那个程序。
    /// 升级过程中托盘可能还是旧的（它不重启），此时报自己的版本就会报错一个。
    /// </summary>
    private static string ReadAgentVersion()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "BackupMonitor.Agent.exe");
            if (File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var version = info.ProductVersion ?? info.FileVersion;
                if (!string.IsNullOrWhiteSpace(version))
                    return "v" + version.Split('+')[0];
            }

            var own = typeof(TrayApplicationContext).Assembly.GetName().Version;
            return own is null ? "版本未知" : "v" + own.ToString(3);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "版本未知";
        }
    }

    private static string? GetArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

    private static string TruncateTooltip(string text) => text.Length <= 63 ? text : text[..60] + "…";

    private static string TruncateBalloon(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";

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
