using System.Diagnostics;

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
    private readonly Button _openWeb = new();
    private readonly Button _diagnostics = new();
    private readonly Button _reissueServerCertificate = new();
    private readonly Button _repairPermissions = new();
    private readonly Button _exportKeyPackage = new();
    private readonly Button _importKeyPackage = new();
    private readonly Button _uninstall = new();
    private readonly ServerMaintenance _maintenance = new();
    private readonly string _installDirectory = ServerMaintenance.DefaultInstallDirectory;
    private readonly string _dataDirectory = ServerMaintenance.DefaultDataDirectory;
    private readonly FlowLayoutPanel _maintenancePanel = new();
    private readonly ServerInstaller _installer = new();

    public SetupForm(string[] args)
    {
        Text = "BackupMonitor Server 安装";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 540);
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
            RowCount = 8
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
        _status.Text = ServerInstaller.IsInstalled()
            ? "检测到已有服务：再次安装将执行修复/升级流程。"
            : "安装目录、数据库目录和端口使用默认值。";
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
        _install.Text = ServerInstaller.IsInstalled() ? "修复/升级" : "安装 Server";
        _install.AutoSize = true;
        _install.Font = new Font(_install.Font, FontStyle.Bold);
        _install.Click += async (_, _) => await InstallAsync();
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_install);
        root.Controls.Add(buttons, 0, 6);

        _maintenancePanel.Dock = DockStyle.Fill;
        _maintenancePanel.AutoSize = true;
        _maintenancePanel.WrapContents = true;
        _maintenancePanel.FlowDirection = FlowDirection.LeftToRight;
        _maintenancePanel.Padding = new Padding(0, 8, 0, 0);
        _resetPassword.Text = "重置管理员密码";
        _restartServices.Text = "重启服务";
        _openWeb.Text = "打开管理网页";
        _diagnostics.Text = "运行诊断";
        _reissueServerCertificate.Text = "重新签发服务端证书";
        _uninstall.Text = "卸载";
        _repairPermissions.Text = "修复文件权限";
        _exportKeyPackage.Text = "导出密钥包";
        _importKeyPackage.Text = "导入密钥包";
        foreach (var button in new[]
        {
            _resetPassword,
            _restartServices,
            _reissueServerCertificate,
            _repairPermissions,
            _exportKeyPackage,
            _importKeyPackage,
            _openWeb,
            _diagnostics,
            _uninstall
        })
        {
            button.AutoSize = true;
            _maintenancePanel.Controls.Add(button);
        }
        _resetPassword.Click += async (_, _) => await ResetPasswordAsync();
        _restartServices.Click += async (_, _) => await RestartServicesAsync();
        _openWeb.Click += (_, _) => OpenWeb();
        _diagnostics.Click += async (_, _) => await ShowDiagnosticsAsync();
        _reissueServerCertificate.Click += async (_, _) => await ReissueServerCertificateAsync();
        _repairPermissions.Click += async (_, _) => await RepairPermissionsAsync();
        _exportKeyPackage.Click += async (_, _) => await ExportKeyPackageAsync();
        _importKeyPackage.Click += async (_, _) => await ImportKeyPackageAsync();
        _uninstall.Click += async (_, _) => await UninstallAsync();
        _maintenancePanel.Enabled = ServerInstaller.IsInstalled();
        root.Controls.Add(_maintenancePanel, 0, 7);
        AcceptButton = _install;
        CancelButton = _cancel;
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
        if (!PrerequisiteCheck.EnsureOrPrompt(this))
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
            _status.Text = item.Message;
            _progress.Value = Math.Clamp(item.Percent, 0, 100);
        });

        SetBusy(true);
        try
        {
            await _installer.InstallAsync(request, progress, CancellationToken.None);
            MessageBox.Show(this, "BackupMonitor Server 安装完成，管理网页即将打开。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败：" + ex.Message;
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _password.Enabled = !busy;
        _confirm.Enabled = !busy;
        _install.Enabled = !busy;
        _maintenancePanel.Enabled = !busy && ServerInstaller.IsInstalled();
        UseWaitCursor = busy;
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
            _status.Text = "管理员密码已重置，现有登录会话已吊销。";
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
            _status.Text = "服务已发出重启命令。";
            MessageBox.Show(this, "PostgreSQL 和 BackupMonitor Server 已重启。", "维护完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重启失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
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
            _status.Text = _maintenance.RepairPermissions(_dataDirectory);
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
            _status.Text = "服务端密钥包已导出；该文件包含敏感密钥，请仅保存到受控位置。";
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
            _status.Text = "服务端密钥包已导入，服务已重启。";
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
            _status.Text = confirm == DialogResult.Yes
                ? "服务和数据已卸载。"
                : "服务已卸载，数据已保留。";
            _maintenancePanel.Enabled = false;
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

    private async Task ReissueServerCertificateAsync()
    {
        if (!ServerInstaller.IsInstalled())
            return;
        var confirm = MessageBox.Show(
            this,
            "重新签发后服务端 TLS 指纹会变化，已安装的 Agent 需要重新运行客户端安装器更新指纹。是否继续？",
            "确认重新签发",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var fingerprint = await _maintenance.ReissueServerCertificateAsync(
                _installDirectory,
                _dataDirectory,
                CancellationToken.None);
            _status.Text = "服务端证书已重新签发，指纹：" + fingerprint;
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
