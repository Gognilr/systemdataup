using System.Diagnostics;

using BackupMonitor.Shared.Security;

namespace BackupMonitor.Server.Setup;

internal sealed class SetupForm : Form
{
    private readonly TextBox _password = new();
    private readonly TextBox _confirm = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _install = new();
    private readonly Button _cancel = new();
    private readonly Button _resetPassword = new();
    private readonly Button _restartServices = new();
    private readonly Button _stopServices = new();
    private readonly Button _startServices = new();
    private readonly Button _openWeb = new();
    private readonly Button _diagnostics = new();
    private readonly Button _showFingerprint = new();
    private readonly Button _reissueServerCertificate = new();
    private readonly Button _stageServerCertificate = new();
    private readonly Button _activateServerCertificate = new();
    private readonly Button _repairPermissions = new();
    private readonly Button _exportKeyPackage = new();
    private readonly Button _importKeyPackage = new();
    private readonly Button _exportBackupPackage = new();
    private readonly Button _importBackupPackage = new();
    private readonly Button _uninstall = new();
    private readonly ServerMaintenance _maintenance = new();
    private readonly string _installDirectory = ServerMaintenance.DefaultInstallDirectory;
    private readonly string _dataDirectory = ServerMaintenance.DefaultDataDirectory;

    private readonly ServerInstaller _installer = new();

    /// <summary>
    /// 服务状态轮询（实施方案 S2）。2 秒一次，只在服务管理台形态下跑。
    ///
    /// 为什么必须是活的：停止/启动/重启这三个按钮该不该点，唯一依据就是当前状态；
    /// 而按下之后服务要几秒才真的起来，静态文本会让人以为没反应、于是再点一次。
    /// </summary>
    private System.Windows.Forms.Timer? _statusTimer;

    /// <summary>已安装形态下，窗口是一台服务控制台而不是安装向导（实施方案 S1）。</summary>
    private bool _consoleMode = ServerInstaller.IsInstalled();

    /// <summary>
    /// 同一条消息在 2 秒轮询里只记一次。状态读取失败往往是持续性的
    /// （服务被删了、权限没了），每两秒往日志里追加一行会把真正做过的操作冲没。
    /// </summary>
    private string? _lastRepeatedKey;

    private void AppendLogOnce(string key, string message)
    {
        if (_lastRepeatedKey == key)
            return;
        _lastRepeatedKey = key;
        AppendLog(message);
    }

    public SetupForm(string[] args)
    {
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScroll = true;
        ShowIcon = true;

        // 装完之后就不该再叫「安装」（实施方案 S1）。
        //
        // 原先两态共用一个窗口：已经装好的机器每次打开，上半屏永远是
        // 「安装 BackupMonitor Server」加两个再也用不到的密码框，而真正要用的
        // 十四个维护按钮挤在最下面一行流式布局里。这不是排版问题，是这个程序
        // 没有承认「它装完之后的身份是服务管理台」。
        if (_consoleMode)
        {
            Text = "BackupMonitor 服务端 · 服务管理";
            ClientSize = new Size(760, 620);
            MinimumSize = new Size(720, 560);
            BuildConsoleLayout();
        }
        else
        {
            Text = "BackupMonitor Server 安装";
            ClientSize = new Size(720, 540);
            MinimumSize = new Size(680, 500);
            BuildInstallLayout();
        }

        WireMaintenanceHandlers();
    }

    // ────────────────────────────────────────────────────────────────────────
    // 安装向导形态
    // ────────────────────────────────────────────────────────────────────────

    private void BuildInstallLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(28, 24, 28, 22),
            ColumnCount = 1,
            RowCount = 7
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 7; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "安装 BackupMonitor Server",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = "只需要设置管理网页登录密码。数据库、内部密钥、Windows 服务和局域网发现由安装器自动配置。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 18)
        }, 0, 1);

        root.Controls.Add(CreatePasswordField("管理网页登录密码", _password, "至少 12 个字符；不会写入日志或安装包。"), 0, 2);
        root.Controls.Add(CreatePasswordField("确认密码", _confirm, "请再次输入相同密码。"), 0, 3);

        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        _status.Dock = DockStyle.Fill;
        _status.Height = 34;
        _status.Margin = new Padding(0, 10, 0, 6);
        _status.ForeColor = Color.DimGray;
        _status.Text = "安装目录、数据库目录和端口使用默认值。";
        root.Controls.Add(_status, 0, 4);

        _progress.Dock = DockStyle.Fill;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Height = 18;
        root.Controls.Add(_progress, 0, 5);

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
        _install.Text = "安装 Server";
        _install.AutoSize = true;
        StylePrimary(_install);
        _install.Click += async (_, _) => await InstallAsync();
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_install);
        root.Controls.Add(buttons, 0, 6);

        AcceptButton = _install;
        CancelButton = _cancel;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 服务管理台形态
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>状态圆点。运行=绿 / 已停止=灰 / 异常=红，三种颜色取自 Web 端的令牌，两端保持同一套。</summary>
    private static readonly Color OkColor = Color.FromArgb(0x0F, 0x7A, 0x4A);      // --ok-fg
    private static readonly Color OffColor = Color.FromArgb(0x7F, 0x8F, 0x99);     // --ink-400
    private static readonly Color ErrColor = Color.FromArgb(0xB3, 0x26, 0x1E);     // --err-fg
    private static readonly Color AccentColor = Color.FromArgb(0x5B, 0x4F, 0xC7);  // --accent
    private static readonly Color BorderColor = Color.FromArgb(0xCC, 0xD5, 0xDA);  // --ink-200
    private static readonly Color MutedColor = Color.FromArgb(0x5E, 0x6D, 0x77);   // --ink-500

    private readonly ServiceIndicator _serverIndicator = new("BackupMonitor.Server");
    private readonly ServiceIndicator _postgresIndicator = new("BackupMonitor.PostgreSQL");
    private readonly TextBox _log = new();

    private void BuildConsoleLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
            ColumnCount = 1,
            RowCount = 4
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 状态条
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 操作分区
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 主按钮提示
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 日志
        Controls.Add(root);

        // ── 顶部状态条（S2）：先知道现在是什么状态，才知道该按哪个按钮。
        // 原先这句话写在注释里，而 FlowLayoutPanel 把状态标签排在十四个按钮之后，
        // 实际渲染位置是最后——注释的意图和布局的效果正好相反。
        var statusBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Color.FromArgb(0xF4, 0xF6, 0xF8)   // --ink-50
        };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statusBar.Controls.Add(_serverIndicator, 0, 0);
        statusBar.Controls.Add(_postgresIndicator, 1, 0);
        root.Controls.Add(statusBar, 0, 0);

        // ── 四组操作（S3）。分组关系原先在视觉上完全不存在：
        // 「启动服务」和「卸载」长得一模一样、还挨着排，而换行位置随窗口宽度漂移。
        var groups = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4
        };
        groups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
            groups.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _install.Text = "修复 / 升级安装";
        _resetPassword.Text = "重置管理员密码";
        _restartServices.Text = "重启服务";
        _stopServices.Text = "停止服务";
        _startServices.Text = "启动服务";
        _openWeb.Text = "打开管理网页";
        _diagnostics.Text = "运行诊断";
        _showFingerprint.Text = "查看服务端指纹";
        _reissueServerCertificate.Text = "重新签发服务端证书";
        _stageServerCertificate.Text = "预备新的服务端证书";
        _activateServerCertificate.Text = "启用预备的服务端证书";
        _uninstall.Text = "卸载";
        _repairPermissions.Text = "修复文件权限";
        _exportKeyPackage.Text = "导出密钥包";
        _importKeyPackage.Text = "导入密钥包";
        _exportBackupPackage.Text = "导出配置备份";
        _importBackupPackage.Text = "导入配置备份";

        groups.Controls.Add(BuildGroup("服务控制",
            [_startServices, _stopServices, _restartServices, _install]), 0, 0);
        groups.Controls.Add(BuildGroup("访问与诊断",
            [_openWeb, _diagnostics, _showFingerprint]), 0, 1);
        groups.Controls.Add(BuildGroup("密钥与配置",
            [_exportKeyPackage, _importKeyPackage, _exportBackupPackage, _importBackupPackage,
             _stageServerCertificate, _activateServerCertificate,
             _reissueServerCertificate, _repairPermissions]), 0, 2);
        // 破坏性操作单独一组、放最下、红色文字（不是红色填充按钮——填充红太吵，
        // 会把注意力从状态条上抢走），两个都要二次确认。
        groups.Controls.Add(BuildGroup("危险操作", [_resetPassword, _uninstall], danger: true), 0, 3);

        root.Controls.Add(groups, 0, 1);

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.Height = 22;
        _status.Margin = new Padding(0, 12, 0, 4);
        _status.ForeColor = MutedColor;
        _status.Text = "";
        root.Controls.Add(_status, 0, 2);

        // ── 常驻日志（S4）。原先 _status 是覆盖式的单行标签：
        // 「服务已发出重启命令」会被下一条「配置备份包已导出」冲掉，
        // 之前发生过什么完全不可考——而导入密钥包、重新签发证书这类做完就不可逆的操作，
        // 「刚才做了什么」必须留痕。只留在本次会话里，不写盘（服务端本身有 logs/ 目录）。
        var logPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 4, 0, 0)
        };
        logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        logPanel.Controls.Add(new Label
        {
            Text = "本次操作记录",
            AutoSize = true,
            ForeColor = MutedColor,
            Font = new Font(Font.FontFamily, 8f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 0);

        var copyLog = new Button { Text = "复制全部", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        StyleSecondary(copyLog);
        // 出问题时管理员要把这一段发给支持，逐行选中复制不现实。
        copyLog.Click += (_, _) =>
        {
            try { Clipboard.SetText(_log.Text); AppendLog("操作记录已复制到剪贴板。"); }
            catch { AppendLog("剪贴板不可用，请手工选中复制。"); }
        };
        logPanel.Controls.Add(copyLog, 1, 0);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.White;
        _log.Font = MonospaceFont(8.5f);
        logPanel.SetColumnSpan(_log, 2);
        logPanel.Controls.Add(_log, 0, 1);
        root.Controls.Add(logPanel, 0, 3);

        // 服务状态必须是活的（S2）。窗口关闭时一并停掉，否则计时器会拖着一个
        // 已经 Dispose 的窗口继续跑。
        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => RefreshServiceStatus();
        _statusTimer.Start();
        FormClosed += (_, _) => { _statusTimer?.Stop(); _statusTimer?.Dispose(); _statusTimer = null; };

        RefreshServiceStatus();
        AppendLog("服务管理台已就绪。");
    }

    /// <summary>
    /// 一组操作：11px 灰色小标题 + 1px 分隔线 + 等宽按钮。
    ///
    /// 用分隔线而不是 GroupBox：后者的凹边框是 Win2000 时代的观感，
    /// 而且它的标题会把整组框起来，视觉重量远超它承载的信息。
    /// 用 TableLayoutPanel 而不是 FlowLayoutPanel：后者的换行位置随窗口宽度漂移，
    /// 同一台机器缩放一下窗口，按钮的相对位置就变了。
    /// </summary>
    private Control BuildGroup(string title, Button[] buttons, bool danger = false)
    {
        const int columns = 4;
        var rows = (buttons.Length + columns - 1) / columns;

        var wrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 14)
        };
        wrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        wrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        wrap.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = danger ? ErrColor : MutedColor,
            Font = new Font(Font.FontFamily, 8f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 2)
        }, 0, 0);

        wrap.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            Height = 1,
            BackColor = BorderColor,
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 1);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            RowCount = rows
        };
        for (var c = 0; c < columns; c++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        for (var r = 0; r < rows; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            button.Dock = DockStyle.Fill;
            button.Height = 30;
            button.Margin = new Padding(0, 0, 8, 6);
            if (danger)
                StyleDanger(button);
            else
                StyleSecondary(button);
            grid.Controls.Add(button, i % columns, i / columns);
        }

        wrap.Controls.Add(grid, 0, 2);
        return wrap;
    }

    /// <summary>
    /// 主按钮跟着状态走（S3）：服务停着时人要做的是把它起来，
    /// 服务跑着时来这个窗口多半是为了开管理网页。
    ///
    /// 十四个同权重的默认按钮是「没设计」最直接的观感来源——它让每一次使用
    /// 都要把全部标签重读一遍才知道该点哪个。
    /// </summary>
    private void UpdatePrimaryButton(bool serverRunning)
    {
        var primary = serverRunning ? _openWeb : _startServices;
        var other = serverRunning ? _startServices : _openWeb;
        if (primary.Tag as string == "primary")
            return;
        StylePrimary(primary);
        StyleSecondary(other);
        primary.Tag = "primary";
        other.Tag = null;
        AcceptButton = primary;
    }

    private static void StylePrimary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = AccentColor;
        button.BackColor = AccentColor;
        button.ForeColor = Color.White;
        button.UseVisualStyleBackColor = false;
        button.Font = new Font(button.Font, FontStyle.Bold);
    }

    private static void StyleSecondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = BorderColor;
        button.BackColor = Color.White;
        button.ForeColor = Color.FromArgb(0x16, 0x1E, 0x23);   // --ink-900
        button.UseVisualStyleBackColor = false;
        button.Font = new Font(button.Font, FontStyle.Regular);
    }

    private static void StyleDanger(Button button)
    {
        StyleSecondary(button);
        button.ForeColor = ErrColor;
        button.FlatAppearance.BorderColor = Color.FromArgb(0xF0, 0xB4, 0xB0);   // --err-bd
    }

    /// <summary>
    /// 等宽字体（S5）。Server 2012 R2 上通常没有 Cascadia Mono，必须留回退链：
    /// 回退失败时 WinForms 会静默给回一个比例字体，而指纹用比例字体逐字核对基本保证看错。
    /// </summary>
    internal static Font MonospaceFont(float size)
    {
        foreach (var family in new[] { "Cascadia Mono", "Consolas", "Courier New" })
        {
            try
            {
                var font = new Font(family, size);
                if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase))
                    return font;
                font.Dispose();
            }
            catch
            {
                // 换下一个
            }
        }
        return new Font(FontFamily.GenericMonospace, size);
    }

    /// <summary>
    /// 一条带时间戳的操作记录（S4）。最多留 200 条，超出丢最旧的。
    /// 安装向导形态下没有日志区，此时只更新那一行状态标签。
    /// </summary>
    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        _status.Text = message;
        if (!_consoleMode)
            return;

        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        var lines = _log.Lines.Where(l => !string.IsNullOrEmpty(l)).Append(line).ToArray();
        if (lines.Length > 200)
            lines = lines[^200..];
        _log.Lines = lines;
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    /// <summary>服务状态一格：圆点 + 服务名 + 状态词。圆点自绘，不用图片资源。</summary>
    private sealed class ServiceIndicator : TableLayoutPanel
    {
        private readonly Panel _dot = new() { Width = 10, Height = 10, Margin = new Padding(0, 6, 8, 0) };
        private readonly Label _name;
        private readonly Label _state = new() { AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        private Color _color = OffColor;

        public ServiceIndicator(string serviceName)
        {
            Dock = DockStyle.Fill;
            AutoSize = true;
            ColumnCount = 3;
            RowCount = 1;
            Margin = new Padding(0);
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _name = new Label
            {
                Text = serviceName,
                AutoSize = true,
                Margin = new Padding(0, 3, 10, 0),
                Font = new Font(Font, FontStyle.Bold)
            };
            _dot.Paint += (_, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(_color);
                e.Graphics.FillEllipse(brush, 0, 0, _dot.Width - 1, _dot.Height - 1);
            };

            Controls.Add(_dot, 0, 0);
            Controls.Add(_name, 1, 0);
            Controls.Add(_state, 2, 0);
        }

        public void Set(string stateText, Color color)
        {
            if (_state.Text == stateText && _color == color)
                return;   // 2 秒一次的轮询，没变就别重绘
            _state.Text = stateText;
            _state.ForeColor = color == OffColor ? MutedColor : color;
            _color = color;
            _dot.Invalidate();
        }
    }

    /// <summary>维护动作的事件接线。两种形态共用同一批按钮对象，接线只做一次。</summary>
    private void WireMaintenanceHandlers()
    {
        _resetPassword.Click += async (_, _) => await ResetPasswordAsync();
        _restartServices.Click += async (_, _) => await RestartServicesAsync();
        _stopServices.Click += async (_, _) => await StopServicesAsync();
        _startServices.Click += async (_, _) => await StartServicesAsync();
        _openWeb.Click += (_, _) => OpenWeb();
        _diagnostics.Click += async (_, _) => await ShowDiagnosticsAsync();
        _showFingerprint.Click += (_, _) => ShowServerFingerprint();
        _reissueServerCertificate.Click += async (_, _) => await ReissueServerCertificateAsync();
        _stageServerCertificate.Click += async (_, _) => await StageServerCertificateAsync();
        _activateServerCertificate.Click += async (_, _) => await ActivateServerCertificateAsync();
        _repairPermissions.Click += async (_, _) => await RepairPermissionsAsync();
        _exportKeyPackage.Click += async (_, _) => await ExportKeyPackageAsync();
        _importKeyPackage.Click += async (_, _) => await ImportKeyPackageAsync();
        _exportBackupPackage.Click += async (_, _) => await ExportBackupPackageAsync();
        _importBackupPackage.Click += async (_, _) => await ImportBackupPackageAsync();
        _uninstall.Click += async (_, _) => await UninstallAsync();

        // 服务管理台形态下「修复/升级安装」也走同一条安装流程。
        if (_consoleMode)
            _install.Click += async (_, _) => await InstallAsync();
    }

    private Control CreatePasswordField(string labelText, TextBox input, string hint)
    {
        var wrap = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        input.UseSystemPasswordChar = true;
        input.Dock = DockStyle.Top;
        input.Height = 28;
        input.Margin = new Padding(0, 0, 0, 3);
        var hintLabel = new Label { Text = hint, AutoSize = true, ForeColor = Color.Gray };
        wrap.Controls.Add(label, 0, 0);
        wrap.Controls.Add(input, 0, 1);
        wrap.Controls.Add(hintLabel, 0, 2);
        return wrap;
    }

    private async Task InstallAsync()
    {
        if (!await PrerequisiteCheck.EnsureOrPromptAsync(this, CancellationToken.None))
            return;

        var upgrade = ServerInstaller.IsInstalled();
        if (!upgrade && _password.Text.Length < 12)
        {
            MessageBox.Show(this, "管理员密码至少需要 12 个字符。", "密码不符合要求", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _password.Focus();
            return;
        }
        if (upgrade && (!string.IsNullOrEmpty(_password.Text) || !string.IsNullOrEmpty(_confirm.Text)))
        {
            MessageBox.Show(
                this,
                "升级/修复不会修改管理员密码。需要改密请使用下方“重置管理员密码”按钮；本次升级请将两个密码框留空。",
                "升级口令说明",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (!upgrade && !string.Equals(_password.Text, _confirm.Text, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "两次输入的密码不一致。", "密码不一致", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _confirm.Focus();
            return;
        }

        var request = new ServerInstallRequest(
            upgrade ? null : _password.Text,
            _installDirectory,
            _dataDirectory);
        var progress = new Progress<ServerInstallProgress>(item =>
        {
            AppendLog(item.Message);
            _progress.Value = Math.Clamp(item.Percent, 0, 100);
        });

        SetBusy(true);
        try
        {
            await _installer.InstallAsync(request, progress, CancellationToken.None);

            // 装完立刻把指纹亮出来：装客户端的人马上就要用它，
            // 不该还得回来自己找一个按钮。这里也顺手复制到剪贴板。
            ShowServerFingerprint();

            MessageBox.Show(this, "BackupMonitor Server 安装完成，管理网页即将打开。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);

            // 装完就不再是安装向导了（实施方案 S1）。原先这里直接 Close()，
            // 人要再点一次这个程序才能看到服务状态和那十几个维护动作；
            // 而刚装完恰恰是最想确认「服务起来没有」的时刻。
            SwitchToConsole();
            OpenWeb();
        }
        catch (Exception ex)
        {
            AppendLog("安装失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>安装成功后就地切换成服务管理台，不需要重开程序。</summary>
    private void SwitchToConsole()
    {
        if (_consoleMode)
            return;

        _consoleMode = true;
        Text = "BackupMonitor 服务端 · 服务管理";
        ClientSize = new Size(760, 620);
        MinimumSize = new Size(720, 560);
        SuspendLayout();
        Controls.Clear();
        BuildConsoleLayout();
        ResumeLayout(performLayout: true);
        SetMaintenanceEnabled(true);
    }

    private void SetBusy(bool busy)
    {
        _password.Enabled = !busy;
        _confirm.Enabled = !busy;
        _install.Enabled = !busy;
        SetMaintenanceEnabled(!busy && ServerInstaller.IsInstalled());
        UseWaitCursor = busy;
    }

    /// <summary>
    /// 维护类按钮整体可用性。服务管理台形态下按钮散在四个分组里、没有一个共同父容器，
    /// 因此逐个开关；卸载按钮不在此列——它自己会把窗口带走。
    /// </summary>
    private bool _maintenanceEnabled = true;

    private void SetMaintenanceEnabled(bool enabled)
    {
        _maintenanceEnabled = enabled;
        foreach (var button in new[]
        {
            _resetPassword, _startServices, _stopServices, _restartServices,
            _showFingerprint, _reissueServerCertificate, _stageServerCertificate, _activateServerCertificate, _repairPermissions,
            _exportKeyPackage, _importKeyPackage, _exportBackupPackage, _importBackupPackage,
            _openWeb, _diagnostics, _uninstall
        })
        {
            button.Enabled = enabled;
        }

        if (enabled)
            ApplyServiceStateToButtons();
    }

    /// <summary>
    /// 最近一次读到的服务端服务运行状态；<c>null</c> = 还没读到 / 读失败。
    /// 三态而不是布尔，是因为「读不到」与「停着」必须区别对待，见 <see cref="ApplyServiceStateToButtons"/>。
    /// </summary>
    private bool? _serverRunning;

    /// <summary>
    /// 按服务状态收窄按钮可用性。
    ///
    /// 原来按钮只跟「是否正忙」「是否已安装」走，状态每 2 秒刷新一次却只用来染色。
    /// 于是服务跑着时点「启动服务」→ sc start 返回 1056（服务已在运行）→
    /// ProcessRunner 默认 throwOnError → 弹一个红叉「启动失败」。那不是失败，
    /// 是这个按钮根本不该能点。
    ///
    /// 两条不能违反的原则：
    /// 1. 忙碌态优先级高于状态态——SetBusy(true) 期间一律禁用，所以这里先看 _maintenanceEnabled；
    /// 2. **状态读取失败时保持全部可用**。读不到状态不该把出路一起藏起来：
    ///    人来这个窗口多半就是因为出了事，这时候把「启动服务」置灰等于把人锁在门外。
    ///    与客户端托盘 catch 分支同一条原则。
    /// </summary>
    private void ApplyServiceStateToButtons()
    {
        if (!_maintenanceEnabled)
            return;

        if (_serverRunning is not { } running)
            return;

        _startServices.Enabled = !running;
        _stopServices.Enabled = running;
        _restartServices.Enabled = running;
        // 服务停着时管理网页必然连不上，点了只会等到超时再报错。
        _openWeb.Enabled = running;
    }

    private async Task ResetPasswordAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;
        if (_password.Text.Length < 12 || !string.Equals(_password.Text, _confirm.Text, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "请在上方输入两次相同的管理员密码（至少 12 个字符）。", "密码不符合要求", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var connection = _maintenance.GetInstalledConnectionString(_installDirectory, _dataDirectory);
            await _maintenance.ResetAdminPasswordAsync(connection, _password.Text, CancellationToken.None);
            AppendLog("管理员密码已重置，现有登录会话已吊销。");
            MessageBox.Show(this, "管理员密码已重置。请使用新密码登录管理网页。", "维护完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RestartServicesAsync()
    {
        SetBusy(true);
        try
        {
            await _maintenance.RestartServicesAsync(CancellationToken.None);
            AppendLog("服务已发出重启命令。");
            MessageBox.Show(this, "PostgreSQL 和 BackupMonitor Server 已重启。", "维护完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重启失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshServiceStatus();
        }
    }

    /// <summary>
    /// 停止服务端。要确认——停止之后管理网页打不开、客户端心跳全部失败，
    /// 这跟「重启」几秒钟的中断不是一回事，人得知道自己在做什么。
    /// </summary>
    private async Task StopServicesAsync()
    {
        var answer = MessageBox.Show(
            this,
            "停止后管理网页将无法访问，所有客户端的心跳、预检和上传都会失败，直到重新启动服务。\n\n"
            + "确定要停止 BackupMonitor Server 和 PostgreSQL 吗？",
            "停止服务",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            await _maintenance.StopServicesAsync(CancellationToken.None);
            AppendLog("服务已停止。");
            MessageBox.Show(this, "BackupMonitor Server 和 PostgreSQL 已停止。", "维护完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "停止失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshServiceStatus();
        }
    }

    private async Task StartServicesAsync()
    {
        SetBusy(true);
        try
        {
            await _maintenance.StartServicesAsync(CancellationToken.None);
            AppendLog("服务已启动。");
            MessageBox.Show(this, "PostgreSQL 和 BackupMonitor Server 已启动。", "维护完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshServiceStatus();
        }
    }

    /// <summary>
    /// 刷新服务状态（实施方案 S2）。2 秒一次由计时器驱动，各操作完成后也会显式调一次。
    ///
    /// 关键约束：**任何路径都不能弹窗、不能抛异常**。它每两秒跑一次，
    /// 一个弹窗就会变成一串弹窗，而查询失败本身并不妨碍其它操作。
    /// </summary>
    private void RefreshServiceStatus()
    {
        if (!ServerInstaller.IsInstalled())
        {
            _serverIndicator.Set("未安装", ErrColor);
            _postgresIndicator.Set("未安装", ErrColor);
            _serverRunning = null;
            return;
        }

        try
        {
            var (server, postgres) = _maintenance.GetServiceStatuses();
            var serverRunning = IsRunning(server);
            _serverIndicator.Set(server, StateColor(server));
            _postgresIndicator.Set(postgres, StateColor(postgres));
            _serverRunning = serverRunning;
            ApplyServiceStateToButtons();
            if (_consoleMode && _maintenanceEnabled)
                UpdatePrimaryButton(serverRunning);
        }
        catch (Exception ex)
        {
            _serverIndicator.Set("读取失败", ErrColor);
            _postgresIndicator.Set("读取失败", ErrColor);
            // 状态读不到就退回「全部可用」：不能因为看不清就把出路一起关掉。
            _serverRunning = null;
            SetMaintenanceEnabled(_maintenanceEnabled);
            AppendLogOnce("readfail", "读取服务状态失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 状态词的判定放宽到「包含 Running / 运行」：GetServiceStatuses 返回的是
    /// ServiceController 的状态名，不同语言的系统上文案不同，
    /// 而这里要的只是「跑没跑起来」这一个二值。判不出来时按「非运行」处理——
    /// 主按钮因此指向「启动服务」，这是两个方向里更安全的那个默认。
    /// </summary>
    private static bool IsRunning(string state) =>
        state.Contains("Running", StringComparison.OrdinalIgnoreCase) || state.Contains("运行");

    private static Color StateColor(string state)
    {
        if (IsRunning(state)) return OkColor;
        if (state.Contains("Stopped", StringComparison.OrdinalIgnoreCase) || state.Contains("停止")) return OffColor;
        return ErrColor;
    }

    private void OpenWeb()
    {
        try
        {
            var port = _maintenance.GetInstalledApiPort(_installDirectory);
            Process.Start(new ProcessStartInfo($"https://127.0.0.1:{port}/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开管理网页", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ShowDiagnosticsAsync()
    {
        SetBusy(true);
        try
        {
            var diagnostics = await _maintenance.GetDiagnosticsAsync(_installDirectory, _dataDirectory, CancellationToken.None);
            var report = diagnostics + Environment.NewLine + Environment.NewLine + PrerequisiteCheck.Summarize();
            MessageBox.Show(this, report, "BackupMonitor 诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "诊断失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RepairPermissionsAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;

        await Task.CompletedTask;

        SetBusy(true);
        try
        {
            AppendLog(_maintenance.RepairPermissions(_dataDirectory));
            MessageBox.Show(this, _status.Text, "维护完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "权限修复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportKeyPackageAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;

        using var dialog = new SaveFileDialog
        {
            Filter = "BackupMonitor 密钥包 (*.bmkp)|*.bmkp|所有文件 (*.*)|*.*",
            FileName = "BackupMonitor-key-package.bmkp",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        SetBusy(true);
        try
        {
            await _maintenance.ExportKeyPackageAsync(_dataDirectory, dialog.FileName, CancellationToken.None);
            AppendLog("服务端密钥包已导出；该文件包含敏感密钥，请仅保存到受控位置。");
            MessageBox.Show(this, _status.Text, "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ImportKeyPackageAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;

        using var dialog = new OpenFileDialog
        {
            Filter = "BackupMonitor 密钥包 (*.bmkp)|*.bmkp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var confirm = MessageBox.Show(
            this,
            "导入会替换当前服务端 secrets、客户端 CA 和 HTTPS 证书，并重启服务。已安装客户端只有在密钥包来自同一实例时才能继续使用。是否继续？",
            "确认导入服务端密钥包",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            await _maintenance.ImportKeyPackageAsync(_dataDirectory, dialog.FileName, CancellationToken.None);
            AppendLog("服务端密钥包已导入，服务已重启。");
            MessageBox.Show(this, _status.Text, "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 导出配置备份包。与密钥包的差别要在文案里说清楚：这个包能把客户端一起带走，
    /// 但它带走的只是「配置与身份」，不含仓库里的备份文件本体。
    /// </summary>
    private async Task ExportBackupPackageAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;

        // 预填到数据目录旁的固定子目录：远程导出的包也落在这里，
        // 两个入口共用一个落点，「上次配置备份是什么时候」才是一笔账而不是两笔。
        // 目录不存在就建出来——否则对话框会退回上次用过的随便哪个目录，
        // 落点一散，超期提醒就永远看不见本机手动导出的那些包。
        var exportDirectory = ConfigBackupPaths.DirectoryFor(_dataDirectory);
        try
        {
            SecureFileSystem.CreateDirectory(exportDirectory, enforceAcl: true, includeCurrentUser: true);
        }
        catch (Exception ex)
        {
            // 建不出来不拦着人导出，只是回到「没有预填」的老行为。
            AppendLog("无法创建默认配置备份目录，将不预填保存位置：" + ex.Message);
            exportDirectory = string.Empty;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "BackupMonitor 配置备份包 (*.bmbp)|*.bmbp|所有文件 (*.*)|*.*",
            FileName = ConfigBackupPaths.BuildFileName(DateTime.Now),
            InitialDirectory = exportDirectory,
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        SetBusy(true);
        try
        {
            await _maintenance.ExportBackupPackageAsync(
                _installDirectory, _dataDirectory, dialog.FileName, CancellationToken.None);
            AppendLog("配置备份包已导出（含服务端密钥与整库转储）；该文件包含敏感密钥，请仅保存到受控位置。"
                + "包内不含 Repository 与 Staging 中的备份文件本体，需另行复制。");
            MessageBox.Show(this, _status.Text, "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ImportBackupPackageAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;

        using var dialog = new OpenFileDialog
        {
            Filter = "BackupMonitor 配置备份包 (*.bmbp)|*.bmbp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var confirm = MessageBox.Show(
            this,
            "导入会替换当前服务端 secrets、客户端 CA、HTTPS 证书，并用备份包中的转储覆盖整个数据库。"
            + "本机现有的任务、计划、客户端和历史记录都将被备份包中的内容取代。\n\n"
            + "恢复前会自动导出一份当前数据库快照。是否继续？",
            "确认导入配置备份包",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var preRestoreDump = await _maintenance.ImportBackupPackageAsync(
                _installDirectory, _dataDirectory, dialog.FileName, CancellationToken.None);
            AppendLog("配置备份包已导入，服务已重启。");
            // 这句必须留在导入成功的提示里：只恢复库不恢复仓库，backup_files 会指向一堆
            // 不存在的文件，而现场直到有人去做恢复时才会发现。
            MessageBox.Show(
                this,
                "配置备份包已导入，服务已重启。已安装的客户端无需重装即可恢复连接。\n\n"
                + "重要：备份包不含仓库(Repository)和暂存区(Staging)中的实际备份文件，"
                + "这两个目录需要管理员从原服务器另行复制到本机数据目录下；"
                + "在复制完成之前，数据库中的备份记录会指向不存在的文件。\n\n"
                + $"恢复前的数据库快照已保存到：\n{preRestoreDump}",
                "导入完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UninstallAsync()
    {
        var confirm = MessageBox.Show(
            this,
            "这将停止并删除 BackupMonitor 两个 Windows 服务及防火墙规则。是否同时删除服务器数据和备份仓库？\n\n选择“是”删除数据，选择“否”保留数据，选择“取消”放弃。",
            "确认卸载",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);
        if (confirm == DialogResult.Cancel)
            return;

        SetBusy(true);
        try
        {
            await _maintenance.UninstallAsync(
                _installDirectory,
                _dataDirectory,
                confirm == DialogResult.Yes,
                CancellationToken.None);
            AppendLog(confirm == DialogResult.Yes
                ? "服务和数据已卸载。"
                : "服务已卸载，数据已保留。");
            SetMaintenanceEnabled(false);
            MessageBox.Show(this, _status.Text, "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 显示服务端 TLS 证书指纹，供操作员带外交给装客户端的人。
    ///
    /// 客户端安装器会显示同一个值并要求确认——两边比对一致才能证明客户端连的是这台
    /// 服务端。局域网里用的是自签名证书，没有公共 CA 背书，这一步是识破中间人的唯一手段：
    /// 中间人能让「响应体里的指纹」和「实际出示的证书」互相自洽，骗不过的只有这个带外取值。
    ///
    /// 顺手复制到剪贴板：把它粘进工单交给装机的人，比让对方照着屏幕抄 64 个字符可靠得多，
    /// 对方在客户端安装器的「高级选项」里粘贴后即为精确比对，连人工核对都省了。
    /// </summary>
    private void ShowServerFingerprint()
    {
        if (!ServerInstaller.IsInstalled())
            return;

        try
        {
            var fingerprint = _maintenance.GetServerCertificateFingerprint(_installDirectory, _dataDirectory);
            var normalized = CertificateFingerprint.Normalize(fingerprint);

            var copied = false;
            try
            {
                Clipboard.SetText(normalized);
                copied = true;
            }
            catch
            {
                // 剪贴板被别的进程占用时不影响查看，下面照常显示。
            }

            // 按 4 位分组（实施方案 S5）：一串 64 位十六进制、不分组，
            // 拿它和部署记录逐字核对基本保证会看错。剪贴板里给的仍是不带空格的原值。
            AppendLog("服务端 TLS 指纹：" + CertificateFingerprint.ToGroupedHex(normalized));
            MessageBox.Show(
                this,
                "服务端 TLS 证书指纹："
                + Environment.NewLine + Environment.NewLine
                + CertificateFingerprint.ToDisplayBlock(normalized)
                + Environment.NewLine + Environment.NewLine
                + "安装客户端时，客户端安装器会显示同一个值并要求核对。"
                + Environment.NewLine
                + "把这个值填进客户端安装器的「高级选项 → 服务端证书指纹」即为精确比对。"
                + Environment.NewLine
                + (copied ? "（已复制到剪贴板）" : "（剪贴板不可用，请手工记录）"),
                "服务端证书指纹",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "读取指纹失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 过渡期第 1 步（R9）：预备一张新证书但不启用，并把它的指纹下发给全部 Agent。
    /// </summary>
    private async Task StageServerCertificateAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;

        var confirm = MessageBox.Show(
            this,
            "这一步会生成一张新的服务端证书，但**不会**启用它——对外仍然使用当前证书，服务不会中断。\n\n"
            + "新指纹会随配置下发给全部客户端，让它们在过渡期内同时接受新旧两个指纹。\n"
            + "等到全部客户端都就绪之后，再点「启用预备的服务端证书」完成切换，全程不必上门。\n\n"
            + "是否现在预备新证书？",
            "预备新的服务端证书",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var fingerprint = await _maintenance.StageServerCertificateAsync(
                _installDirectory, _dataDirectory, CancellationToken.None);
            AppendLog("已预备新的服务端证书，指纹：" + fingerprint);
            MessageBox.Show(
                this,
                "新证书已预备好，指纹：\n" + fingerprint
                + "\n\n客户端会在下一次心跳时拿到它。请过一段时间后再点「启用预备的服务端证书」，"
                + "那一步会先告诉你有多少台客户端已经就绪。",
                "预备完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "预备失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 过渡期第 4 步（R9）：确认就绪台数之后启用预备证书。
    ///
    /// 就绪台数是这一步唯一的判据：没就绪的机器会在切换那一刻掉线，
    /// 而且再也连不回来（连不上就收不到新指纹），只能上门重跑安装器。
    /// 所以这里必须把那个数字摆在人眼前，而不是问一句「是否继续」。
    /// </summary>
    private async Task ActivateServerCertificateAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;

        SetBusy(true);
        ServerMaintenance.CertificateRotationStatus status;
        try
        {
            status = await _maintenance.GetCertificateRotationStatusAsync(
                _installDirectory, _dataDirectory, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, ex.Message, "读取过渡状态失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        if (status.StagedFingerprint is null)
        {
            MessageBox.Show(
                this,
                "当前没有预备的服务端证书。请先点「预备新的服务端证书」。",
                "没有可启用的证书", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var notReady = status.TotalClients - status.ReadyClients;
        var warning = notReady > 0
            ? $"\n\n⚠ 还有 {notReady} 台客户端没有拿到新指纹。切换之后它们会连不上，"
              + "而且**再也连不回来**——只能到每一台机器上重跑客户端安装器。\n"
              + "除非确认这几台本来就已经下线不用了，否则请再等一等。"
            : "\n\n全部客户端都已就绪，可以安全切换。";

        var confirm = MessageBox.Show(
            this,
            $"预备证书指纹：\n{status.StagedFingerprint}\n\n"
            + $"客户端就绪情况：{status.ReadyClients} / {status.TotalClients}"
            + warning
            + "\n\n启用会替换当前证书并重启服务端（管理网页会短暂中断）。是否继续？",
            "启用预备的服务端证书",
            MessageBoxButtons.YesNo,
            notReady > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var fingerprint = await _maintenance.ActivateStagedServerCertificateAsync(
                _installDirectory, _dataDirectory, CancellationToken.None);
            AppendLog("预备证书已启用，当前指纹：" + fingerprint);
            MessageBox.Show(
                this,
                "新证书已启用，当前指纹：\n" + fingerprint
                + "\n\n旧证书已另存为 server-certificate.pfx.previous——切换后如果出了问题，"
                + "把它复制回 server-certificate.pfx 再重启服务就能退回去。",
                "启用完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "启用失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReissueServerCertificateAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;
        // R9：这个按钮是**一步到位**的重新签发，指纹立刻变，全网 Agent 立刻掉线。
        // 它保留下来只为「反正也没有客户端了」这种场景；正常轮换应该走
        // 预备 → 等就绪 → 启用 这条不必上门的路。文案必须把这个岔路口说清楚，
        // 否则人会照着最显眼的那个按钮点下去，然后面对几十台连不上的机器。
        var confirm = MessageBox.Show(
            this,
            "⚠ 这个按钮会立刻更换服务端证书，指纹随即变化。\n\n"
            + "已安装的 Agent 全部按指纹固定连接，切换之后它们会立刻连不上，"
            + "而且**连不上就收不到新指纹**——只能到每一台机器上重跑客户端安装器。\n\n"
            + "如果这台服务端已经有客户端在用，请改用「预备新的服务端证书」→ 等客户端就绪 → "
            + "「启用预备的服务端证书」，那条路全程不必上门。\n\n"
            + "确定仍要立即重新签发吗？",
            "确认重新签发",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var fingerprint = await _maintenance.ReissueServerCertificateAsync(
                _installDirectory,
                _dataDirectory,
                CancellationToken.None);
            AppendLog("服务端证书已重新签发，指纹：" + fingerprint);
            MessageBox.Show(this, "服务端证书已重新签发并重启服务。请重新运行 Agent 安装器更新证书指纹。", "维护完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重新签发失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
