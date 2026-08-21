using Microsoft.Win32;

namespace BackupMonitor.Agent.Setup;

internal sealed class SetupForm : Form
{
    private readonly TextBox _serverUrl = new();
    private readonly TextBox _displayName = new();
    private readonly TextBox _registrationToken = new();
    private readonly ComboBox _discoveredServers = new();
    private readonly Button _rescan = new();
    private readonly Button _advancedToggle = new();
    private readonly Control _advancedPanel;
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _install = new();
    private readonly Button _uninstall = new();
    private readonly Button _test = new();
    private readonly Button _cancel = new();
    private readonly AgentInstaller _installer = new();
    private readonly LanDiscoveryClient _discovery = new();

    public SetupForm()
    {
        Text = "BackupMonitor Agent 安装";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(740, 560);
        MinimumSize = new Size(680, 500);
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScroll = true;
        ShowIcon = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(28, 24, 28, 22),
            ColumnCount = 1,
            RowCount = 9
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            Text = "安装 BackupMonitor Agent",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6)
        };
        root.Controls.Add(title, 0, 0);

        var intro = new Label
        {
            Text = "安装程序会自动发现服务端并创建服务、配置托盘和自动登记；发现失败时才需要填写地址。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 18)
        };
        root.Controls.Add(intro, 0, 1);

        root.Controls.Add(CreateField("服务端地址（自动发现失败时填写）", _serverUrl, "例如 https://192.168.1.20:5080"), 0, 2);
        root.Controls.Add(CreateField("客户端名称", _displayName, "默认使用当前电脑名称"), 0, 3);

        var discoveryRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 12)
        };
        _discoveredServers.DropDownStyle = ComboBoxStyle.DropDownList;
        _discoveredServers.Width = 470;
        _discoveredServers.SelectedIndexChanged += (_, _) =>
        {
            if (_discoveredServers.SelectedItem is DiscoveredServer server)
                _serverUrl.Text = server.ApiAddress;
        };
        _rescan.Text = "重新扫描";
        _rescan.AutoSize = true;
        _rescan.Click += async (_, _) => await DiscoverAsync();
        _advancedToggle.Text = "高级选项";
        _advancedToggle.AutoSize = true;
        _advancedToggle.Click += (_, _) => ToggleAdvanced();
        discoveryRow.Controls.Add(_discoveredServers);
        discoveryRow.Controls.Add(_rescan);
        discoveryRow.Controls.Add(_advancedToggle);
        root.Controls.Add(discoveryRow, 0, 4);

        _advancedPanel = CreateField("注册令牌（Secure 高级模式）", _registrationToken, "LAN 自动登记模式无需填写；仅在 Secure 模式下使用。\n");
        _registrationToken.UseSystemPasswordChar = true;
        _advancedPanel.Visible = false;
        root.Controls.Add(_advancedPanel, 0, 5);

        _status.AutoSize = true;
        _status.ForeColor = Color.DimGray;
        _status.Text = "安装前会自动从服务端获取安全配置，请保持网络畅通。";
        _status.Margin = new Padding(0, 14, 0, 8);
        root.Controls.Add(_status, 0, 6);

        _progress.Dock = DockStyle.Fill;
        _progress.Height = 18;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Value = 0;
        root.Controls.Add(_progress, 0, 7);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 18, 0, 0)
        };
        _cancel.Text = "取消";
        _cancel.AutoSize = true;
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Click += (_, _) => Close();

        _install.Text = "安装 Agent";
        _install.AutoSize = true;
        _install.Font = new Font(_install.Font, FontStyle.Bold);
        _install.Click += async (_, _) => await InstallAsync();

        _uninstall.Text = "卸载 Agent";
        _uninstall.AutoSize = true;
        _uninstall.Visible = false;
        _uninstall.Click += async (_, _) => await UninstallAsync();

        _test.Text = "测试连接";
        _test.AutoSize = true;
        _test.Click += async (_, _) => await TestConnectionAsync();

        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_uninstall);
        buttons.Controls.Add(_install);
        buttons.Controls.Add(_test);
        root.Controls.Add(buttons, 0, 8);

        AcceptButton = _install;
        CancelButton = _cancel;
        Load += (_, _) => LoadDefaults();
        Load += (_, _) => UpdateMaintenanceState();
        Load += async (_, _) => await DiscoverAsync();
    }

    private Control CreateField(string labelText, TextBox input, string hint)
    {
        var wrap = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };
        wrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        input.Dock = DockStyle.Top;
        input.Height = 28;
        input.Margin = new Padding(0, 0, 0, 3);
        input.Font = new Font(Font.FontFamily, 10);
        var hintLabel = new Label
        {
            Text = hint,
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font(Font.FontFamily, 8),
            Margin = new Padding(0)
        };
        wrap.Controls.Add(label, 0, 0);
        wrap.Controls.Add(input, 0, 1);
        wrap.Controls.Add(hintLabel, 0, 2);
        return wrap;
    }

    private void LoadDefaults()
    {
        _displayName.Text = Environment.MachineName;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\BackupMonitor\AgentSetup");
            _serverUrl.Text = key?.GetValue("ServerUrl") as string ?? "https://127.0.0.1:5080";
        }
        catch
        {
            _serverUrl.Text = "https://127.0.0.1:5080";
        }
        _serverUrl.SelectAll();
        _serverUrl.Focus();
    }

    private async Task TestConnectionAsync()
    {
        if (!TryReadInputs(out var serverUrl, out _, out _))
            return;

        SetBusy(true, "正在测试服务端连接…");
        try
        {
            await _installer.GetBootstrapAsync(serverUrl, CancellationToken.None);
            SetStatus("连接成功，可以开始安装。", Color.DarkGreen, 0);
            MessageBox.Show(this, "服务端连接成功，安全配置可用。", "连接成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("连接失败：" + ex.Message, Color.Firebrick, 0);
            MessageBox.Show(this, ex.Message, "无法连接服务端", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DiscoverAsync()
    {
        _rescan.Enabled = false;
        _discoveredServers.Enabled = false;
        SetStatus("正在扫描局域网 BackupMonitor Server…", Color.DimGray, 2);
        try
        {
            var servers = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            _discoveredServers.Items.Clear();
            foreach (var server in servers)
                _discoveredServers.Items.Add(server);

            _discoveredServers.DisplayMember = nameof(DiscoveredServer.DisplayName);
            if (servers.Count == 1)
            {
                _discoveredServers.SelectedIndex = 0;
                SetStatus("已发现 1 台服务端，可以直接安装。", Color.DarkGreen, 5);
            }
            else if (servers.Count > 1)
            {
                _serverUrl.Clear();
                SetStatus($"已发现 {servers.Count} 台服务端，请选择目标。", Color.DarkOrange, 5);
            }
            else
            {
                SetStatus("未发现服务端；可以在上方手工填写地址后继续。", Color.DarkOrange, 5);
            }
        }
        catch (Exception ex)
        {
            SetStatus("自动发现失败，请手工填写服务端地址：" + ex.Message, Color.DarkOrange, 5);
        }
        finally
        {
            _rescan.Enabled = true;
            _discoveredServers.Enabled = true;
        }
    }

    private async Task InstallAsync()
    {
        if (!TryReadInputs(out var serverUrl, out var displayName, out var registrationToken))
            return;

        var progress = new Progress<InstallProgress>(item => SetStatus(item.Message, Color.DimGray, item.Percent));
        SetBusy(true, "正在准备安装…");
        try
        {
            await _installer.InstallAsync(serverUrl, displayName, registrationToken, progress, CancellationToken.None);
            SetStatus("安装完成，正在自动登记并上线。", Color.DarkGreen, 100);
            MessageBox.Show(
                this,
                "BackupMonitor Agent 已安装并启动。\n\n安装器已提交自动登记，服务端上线后会在客户端页面显示这台电脑。",
                "安装完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
            UpdateMaintenanceState();
        }
        catch (Exception ex)
        {
            SetStatus("安装失败：" + ex.Message, Color.Firebrick, 0);
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UninstallAsync()
    {
        var choice = MessageBox.Show(
            this,
            "将停止并删除 BackupMonitor Agent 服务、托盘自启动和客户端文件。\n\n选择“是”同时删除客户端运行数据和本地证书；选择“否”保留运行数据；选择“取消”放弃。",
            "卸载 Agent",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (choice == DialogResult.Cancel)
            return;

        SetBusy(true, "正在卸载 Agent…");
        try
        {
            await _installer.UninstallAsync(choice == DialogResult.Yes, CancellationToken.None);
            SetStatus("Agent 已卸载。", Color.DarkGreen, 100);
            MessageBox.Show(this, "Agent 服务、托盘自启动和安装文件已移除。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateMaintenanceState();
        }
        catch (Exception ex)
        {
            SetStatus("卸载失败：" + ex.Message, Color.Firebrick, 0);
            MessageBox.Show(this, ex.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateMaintenanceState()
    {
        var installed = AgentInstaller.IsInstalled();
        _uninstall.Visible = installed;
        _install.Text = installed ? "修复/重新安装" : "安装 Agent";
        if (installed)
            SetStatus("检测到已安装的 Agent，可修复/重新安装或卸载。", Color.DimGray, _progress.Value);
    }

    private bool TryReadInputs(out string serverUrl, out string displayName, out string registrationToken)
    {
        serverUrl = _serverUrl.Text.Trim().TrimEnd('/');
        displayName = string.IsNullOrWhiteSpace(_displayName.Text) ? Environment.MachineName : _displayName.Text.Trim();
        registrationToken = _registrationToken.Text.Trim();

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "请输入有效的 HTTP 或 HTTPS 服务端地址。", "输入不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _serverUrl.Focus();
            return false;
        }

        return true;
    }

    private void ToggleAdvanced()
    {
        _advancedPanel.Visible = !_advancedPanel.Visible;
        _advancedToggle.Text = _advancedPanel.Visible ? "隐藏高级选项" : "高级选项";
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _serverUrl.Enabled = !busy;
        _displayName.Enabled = !busy;
        _registrationToken.Enabled = !busy && _advancedPanel.Visible;
        _discoveredServers.Enabled = !busy;
        _rescan.Enabled = !busy;
        _advancedToggle.Enabled = !busy;
        _install.Enabled = !busy;
        _uninstall.Enabled = !busy;
        _test.Enabled = !busy;
        if (message is not null)
            SetStatus(message, Color.DimGray, busy ? Math.Max(1, _progress.Value) : _progress.Value);
        UseWaitCursor = busy;
    }

    private void SetStatus(string message, Color color, int percent)
    {
        _status.Text = message;
        _status.ForeColor = color;
        _progress.Value = Math.Clamp(percent, 0, 100);
    }
}
