using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;

namespace BackupMonitor.Agent.Updater;

/// <summary>升级执行器的退出码。</summary>
internal enum UpgradeOutcome
{
    Succeeded = 0,
    RolledBack = 1,
    NotStarted = 2
}

/// <summary>
/// 一次升级的全过程：停服务 → 确认真的停了 → 备份当前目录 → 覆盖新文件 →
/// 启动服务 → 等心跳恢复并确认新版本号 → 删除备份。
///
/// **回滚是这件事的命门**：任一步失败立刻把旧目录换回来并重新启动服务。
/// 升级失败 = Agent 起不来 = 这台机器再也收不到任何指令，**包括修复指令**——
/// 它连不上，就没法远程救它，只能上门。所以回滚路径上刻意只有三个动作
/// （删新目录、把备份目录改回原名、启动服务），不调用任何可能失败的复杂逻辑。
///
/// 「备份」用的是同级目录改名而不是逐文件复制：改名是一次元数据操作，
/// 不占空间也几乎不会半途而废，而 502 个文件的复制在磁盘满的时候会失败在中间。
/// </summary>
internal sealed class UpgradeRunner
{
    /// <summary>等服务停稳 / 起来的上限。</summary>
    private static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 等新 Agent 心跳恢复的上限。5 分钟：新进程起来后要重建 TLS、
    /// 拉一次配置才会发第一次心跳，而心跳间隔本身默认就是 60 秒。
    /// </summary>
    private static readonly TimeSpan HeartbeatWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 升级包里不覆盖到安装目录的目录名。
    ///
    /// updater 自己就装在安装目录之外，但升级包里带着它的一份新副本（给全新安装用）。
    /// 把它连同新文件一起铺进安装目录没有意义，反而会让安装目录里多出一个
    /// 永远不会被执行的副本。**updater 自身的更新只随完整重装发生**，这是已知取舍。
    /// </summary>
    private const string UpdaterFolderName = "updater";

    /// <summary>
    /// 包里不铺进安装目录的文件，与 AgentInstaller.CopyPayload 的口径一致。
    /// 让「升级出来的安装目录」和「全新装出来的安装目录」长得一样——
    /// 两者有差异时，现场排障会多出一整类没人想得到的可能性。
    /// </summary>
    private static readonly string[] SkippedFiles =
        ["install-agent.ps1", "uninstall-agent.ps1", "README.md"];

    private readonly UpdaterOptions _options;
    private readonly UpdaterLog _log;
    private readonly string _backupDirectory;

    public UpgradeRunner(UpdaterOptions options, UpdaterLog log)
    {
        _options = options;
        _log = log;
        _backupDirectory = _options.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar) + ".backup";
    }

    public UpgradeOutcome Run()
    {
        var startedAt = DateTime.UtcNow;

        // ── 前置检查：这一段之前一个字节都还没动过，失败就干脆什么都不做 ──────────
        if (!Directory.Exists(_options.InstallDirectory))
        {
            Fail("NotStarted", $"安装目录不存在：{_options.InstallDirectory}");
            return UpgradeOutcome.NotStarted;
        }

        if (!Directory.Exists(_options.SourceDirectory))
        {
            Fail("NotStarted", $"升级包目录不存在：{_options.SourceDirectory}");
            return UpgradeOutcome.NotStarted;
        }

        foreach (var required in new[] { "BackupMonitor.Agent.exe", "BackupMonitor.Agent.dll" })
        {
            if (!File.Exists(Path.Combine(_options.SourceDirectory, required)))
            {
                // 哈希对但内容不是一份能跑的发布——停在这里，旧版本原样继续跑。
                Fail("NotStarted", $"升级包缺少 {required}，不是一份可运行的自包含发布，已放弃升级");
                return UpgradeOutcome.NotStarted;
            }
        }

        try
        {
            StopTray();

            _log.Info("正在停止 Agent 服务…");
            if (!StopService())
            {
                Fail("NotStarted", $"{ServiceWait.TotalSeconds:F0} 秒内没能停下 Agent 服务，已放弃升级（旧版本继续运行）");
                TryStartService();
                return UpgradeOutcome.NotStarted;
            }

            // ── 从这里开始改动磁盘，后面每一步的失败都要走回滚 ───────────────────
            PrepareBackupSlot();
            _log.Info($"正在把当前安装目录改名为备份：{_backupDirectory}");
            Directory.Move(_options.InstallDirectory, _backupDirectory);
        }
        catch (Exception ex)
        {
            // 改名失败说明还有进程锁着目录里的文件。目录本身没动，直接把服务拉回来。
            _log.Error("备份当前安装目录失败", ex);
            TryStartService();
            Fail("NotStarted", $"备份当前安装目录失败，已放弃升级：{ex.Message}");
            return UpgradeOutcome.NotStarted;
        }

        try
        {
            _log.Info("正在复制新版本文件…");
            CopyPayload();
            PreserveLocalSettings();

            // 暂存记录必须在**拉起服务之前**删掉。
            //
            // 文件已经铺完，这条记录的任务就结束了；而下一行启动的新 Agent 会在零点几秒内
            // 读 pending-update.json。留到后面的收尾阶段再删，就等于给新 Agent 留了一个窗口：
            // 它读到这条记录，拉起第二个 updater 把服务停掉，本进程的 WaitForHeartbeat 因此
            // 永远等不到心跳，也就永远走不到收尾那几行——记录永远删不掉，循环永远停不下来。
            // 2026-09-11 OA 服务器就是这样空转了 312 圈。
            TryDeleteFile(Path.Combine(_options.DataDirectory, "pending-update.json"));

            _log.Info("正在启动 Agent 服务…");
            if (!StartService())
                throw new InvalidOperationException($"{ServiceWait.TotalSeconds:F0} 秒内服务没有进入运行状态");

            var running = ReadInstalledVersion();
            _log.Info($"安装目录里的版本号：{running ?? "读不到"}");

            if (!WaitForHeartbeat(startedAt))
                throw new InvalidOperationException($"{HeartbeatWait.TotalMinutes:F0} 分钟内没有等到新版本的心跳");

            _log.Info("升级完成，正在清理备份与暂存包…");
            TryDeleteDirectory(_backupDirectory);
            TryDeleteDirectory(_options.SourceDirectory);
            UpgradeResultFile.Write(_options, "succeeded", $"已切换到 {running ?? _options.TargetVersion}", _log);

            // 托盘放在最后且失败不影响结论：它是登录会话里的另一个进程，
            // 换没换过去跟备份能不能做没有关系，而且下次登录时自启项会把它拉起来。
            RestartTray();
            return UpgradeOutcome.Succeeded;
        }
        catch (Exception ex)
        {
            _log.Error("升级失败，开始回滚", ex);
            var rolledBack = Rollback();
            Fail(rolledBack ? "rolled_back" : "failed",
                rolledBack
                    ? $"升级失败已回滚到旧版本：{ex.Message}"
                    : $"升级失败且回滚也没成功，这台机器需要人工处理：{ex.Message}");
            RestartTray();
            return UpgradeOutcome.RolledBack;
        }
    }

    /// <summary>
    /// 回滚：删新目录 → 把备份目录改回原名 → 启动服务。
    /// 只有这三步，而且每一步都单独 try——回滚路径上任何一次未捕获的异常
    /// 都意味着这台机器从此没有 Agent。
    /// </summary>
    private bool Rollback()
    {
        try
        {
            StopService();
        }
        catch (Exception ex)
        {
            _log.Warn($"回滚前停止服务失败（继续回滚）：{ex.Message}");
        }

        try
        {
            if (Directory.Exists(_options.InstallDirectory))
                Directory.Delete(_options.InstallDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Error("回滚时删除新目录失败", ex);
            return false;
        }

        try
        {
            if (Directory.Exists(_backupDirectory))
                Directory.Move(_backupDirectory, _options.InstallDirectory);
        }
        catch (Exception ex)
        {
            _log.Error("回滚时还原旧目录失败", ex);
            return false;
        }

        var started = TryStartService();
        _log.Info(started ? "回滚完成，旧版本已重新启动" : "回滚已还原文件，但服务没能启动");
        return started;
    }

    private void CopyPayload()
    {
        Directory.CreateDirectory(_options.InstallDirectory);
        foreach (var file in Directory.EnumerateFiles(_options.SourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_options.SourceDirectory, file);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (string.Equals(firstSegment, UpdaterFolderName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (SkippedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                continue;

            var destination = Path.Combine(_options.InstallDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>
    /// 保留本机的 appsettings.json。
    ///
    /// 包里那一份是模板（服务端地址、签名公钥、证书指纹全是占位值）。
    /// 拿它盖掉本机的配置，Agent 起来之后会连一个不存在的服务端——
    /// 而那正是「升级完就失联」最容易发生的地方。
    /// </summary>
    private void PreserveLocalSettings()
    {
        const string settingsName = "appsettings.json";
        var previous = Path.Combine(_backupDirectory, settingsName);
        if (!File.Exists(previous))
        {
            _log.Warn("备份目录里没有 appsettings.json，沿用升级包里的那一份");
            return;
        }

        File.Copy(previous, Path.Combine(_options.InstallDirectory, settingsName), overwrite: true);
        _log.Info("已保留本机原有的 appsettings.json");
    }

    /// <summary>上一次升级留下的备份目录先清掉，否则改名会撞名字。</summary>
    private void PrepareBackupSlot()
    {
        if (!Directory.Exists(_backupDirectory))
            return;

        _log.Warn($"发现上一次升级遗留的备份目录，先删除：{_backupDirectory}");
        Directory.Delete(_backupDirectory, recursive: true);
    }

    private bool StopService()
    {
        using var service = new ServiceController(_options.ServiceName);
        if (service.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending)
            service.Stop();

        try
        {
            service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceWait);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return false;
        }

        // 服务状态变成 Stopped 之后进程还可能在退出的最后几百毫秒里，
        // 这段时间它仍然锁着 dll。确认进程真的没了再动文件——
        // 否则复制会失败在中间，而那时目录已经是一半新一半旧。
        return WaitForProcessExit("BackupMonitor.Agent", TimeSpan.FromSeconds(30));
    }

    private bool StartService()
    {
        using var service = new ServiceController(_options.ServiceName);
        service.Refresh();
        if (service.Status is not ServiceControllerStatus.Running and not ServiceControllerStatus.StartPending)
            service.Start();

        try
        {
            service.WaitForStatus(ServiceControllerStatus.Running, ServiceWait);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }

    private bool TryStartService()
    {
        try
        {
            return StartService();
        }
        catch (Exception ex)
        {
            _log.Error("启动 Agent 服务失败", ex);
            return false;
        }
    }

    private static bool WaitForProcessExit(string processName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName(processName).Length == 0)
                return true;
            Thread.Sleep(500);
        }

        return Process.GetProcessesByName(processName).Length == 0;
    }

    /// <summary>
    /// 等新 Agent 的心跳恢复。
    ///
    /// 判据是 state.json 里的 LastHeartbeatAtUtc 比升级开始的时刻新——
    /// 「服务进入 Running」只能证明进程起来了，证明不了它连得上服务端。
    /// 而升级最要命的失败形态恰恰是「起来了但从此连不上」：
    /// 那种机器在服务管理台上是绿的，在服务端上是失联的。
    /// </summary>
    private bool WaitForHeartbeat(DateTime startedAtUtc)
    {
        var statePath = Path.Combine(_options.DataDirectory, "state.json");
        var deadline = DateTime.UtcNow + HeartbeatWait;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(statePath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(statePath));
                    if (document.RootElement.TryGetProperty("LastHeartbeatAtUtc", out var value)
                        && value.ValueKind is JsonValueKind.String
                        && DateTime.TryParse(value.GetString(), null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal
                            | System.Globalization.DateTimeStyles.AssumeUniversal, out var last)
                        && last > startedAtUtc)
                    {
                        _log.Info($"新版本心跳已恢复：{last:O}");
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Agent 正在写这个文件时读到半份是正常的，下一轮再读。
            }

            Thread.Sleep(5000);
        }

        return false;
    }

    private string? ReadInstalledVersion()
    {
        try
        {
            var exe = Path.Combine(_options.InstallDirectory, "BackupMonitor.Agent.exe");
            return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 托盘是登录用户会话里的另一个进程，且它就跑在安装目录里，
    /// 不停掉它，紧接着的目录改名一定会因为 WinForms 程序集被锁而失败
    /// （客户端安装器里踩过同一个坑，那里有注释）。
    /// </summary>
    private void StopTray()
    {
        foreach (var process in Process.GetProcessesByName("BackupMonitor.Agent.Tray"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(10000);
                _log.Info($"已停止托盘进程 {process.Id}");
            }
            catch (Exception ex)
            {
                _log.Warn($"停止托盘进程失败：{ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void RestartTray()
    {
        var trayPath = Path.Combine(_options.InstallDirectory, "BackupMonitor.Agent.Tray.exe");
        if (!File.Exists(trayPath))
            return;

        var arguments = $"--service-name \"{_options.ServiceName}\" --data-directory \"{_options.DataDirectory}\"";
        if (SessionLauncher.TryLaunchInConsoleSession(trayPath, arguments, _log))
        {
            _log.Info("托盘已在当前登录的会话里重新启动");
        }
        else
        {
            // Warn 而不是 Info：托盘是这台机器上唯一有人会看的界面，
            // 它没起来这件事值得在日志里留一条显眼的记录，而不是一句轻描淡写的信息。
            _log.Warn(
                "托盘没能重新启动（没有正在使用中的登录会话，或启动被拒）。"
                + "下次登录时会由自启项拉起；要立刻恢复，手工运行一次 " + trayPath);
        }
    }

    private void Fail(string status, string message)
    {
        _log.Error(message);
        UpgradeResultFile.Write(_options, status, message, _log);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // 清理失败不影响升级结果本身，但要留痕：残留的备份目录会占掉一份安装体积。
            _log.Warn($"删除目录失败 {path}：{ex.Message}");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"删除文件失败 {path}：{ex.Message}");
        }
    }
}
