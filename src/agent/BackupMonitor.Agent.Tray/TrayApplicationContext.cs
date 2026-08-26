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
        _refreshItem = new ToolStripMenuItem("刷新状态", null, async (_, _) => await RefreshStatusAsync());

        // 托盘此前只能"停止服务并退出"。停完之后托盘也没了，想再把服务起回来
        // 只剩 services.msc 这一条路——而托盘的使用者恰恰是最不该被送去那里的人。
        // 这两项按当前状态互斥显示：停着的时候只给"启动"，跑着的时候只给"停止"。
        _startServiceItem = new ToolStripMenuItem("启动 Agent 服务", null, async (_, _) => await ToggleServiceAsync(start: true));
        _stopServiceItem = new ToolStripMenuItem("停止 Agent 服务（托盘保留）", null, async (_, _) => await ToggleServiceAsync(start: false));

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("BackupMonitor Agent") { Enabled = false });
        menu.Items.Add(_statusItem);
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
            var (label, color) = status switch
            {
                ServiceControllerStatus.Running => ("运行中", Color.SeaGreen),
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ("启动中", Color.DarkOrange),
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ("停止中", Color.DarkOrange),
                ServiceControllerStatus.Stopped => ("已停止", Color.Firebrick),
                _ => ("状态未知", Color.DarkGray)
            };

            _statusItem.Text = $"状态：{label}";
            _notifyIcon.Text = TruncateTooltip($"BackupMonitor Agent：{label}");
            ReplaceIcon(CreateStatusIcon(color));
            _refreshItem.Enabled = true;
            UpdateServiceMenuItems(status);
        }
        catch (Exception ex)
        {
            _statusItem.Text = "状态：无法读取服务";
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
