using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>已下载校验完毕、等待空闲窗口安装的升级包（data 目录下的 pending-update.json）。</summary>
public sealed class PendingUpdate
{
    public string Version { get; set; } = string.Empty;

    /// <summary>解压出来的载荷目录；updater 从这里往安装目录铺文件。</summary>
    public string PayloadDirectory { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public DateTime StagedAtUtc { get; set; }

    /// <summary>下发这次升级的指令 ID，原样带给 updater，再由新 Agent 回报给服务端。</summary>
    public Guid? CommandId { get; set; }

    /// <summary>
    /// 已经拉起过几次 updater。到达上限就放弃这个包。
    ///
    /// 没有这个计数时，任何一种「装不上」都会变成无限重试：updater 失败后不会删
    /// pending-update.json，Agent 重启后又读到它，再拉一次 updater——2026-09-11
    /// OA 服务器这样空转了 312 圈，两个半小时。
    /// </summary>
    public int AttemptCount { get; set; }
}

/// <summary>updater 写下的升级结果（data 目录下的 upgrade-result.json）。</summary>
public sealed class UpgradeResultRecord
{
    public string Status { get; set; } = string.Empty;

    public string? TargetVersion { get; set; }

    public string? CommandId { get; set; }

    public string? Message { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// 升级暂存包的落地与执行（整改清单 2026-09-10 · R20）。
///
/// 在这次整改之前，<c>pending-update.json</c> **全仓库只有一处写入、零个读取方**：
/// Agent 下载、校验、解压、写下这个文件，然后回报「升级成功」——
/// 而运行的程序一个字节没变。这个类就是当时缺失的那半边：把暂存包真正装上去。
///
/// 装的动作本身不在这里做，也不可能在这里做：**Windows 服务不能替换自己正在运行的
/// 程序文件**。这里只负责在空闲窗口把 BackupMonitor.Agent.Updater 拉起来，
/// 它住在安装目录之外，不会被自己要替换的文件锁住。
/// </summary>
public sealed class AgentUpgradeCoordinator
{
    public const string PendingFileName = "pending-update.json";

    public const string ResultFileName = "upgrade-result.json";

    private const string UpdaterFileName = "BackupMonitor.Agent.Updater.exe";

    /// <summary>updater 进程名（不含扩展名），用来判断它是否还在收尾。</summary>
    private const string UpdaterProcessName = "BackupMonitor.Agent.Updater";

    /// <summary>升级包里存放 updater 的子目录名。</summary>
    private const string UpdaterPayloadFolderName = "updater";

    /// <summary>数据目录下存放升级包的目录名。</summary>
    private const string UpdatesFolderName = "updates";

    /// <summary>数据目录下暂存「待换上去的新版 updater」的目录名。</summary>
    private const string StagedUpdaterFolderName = "updater-staged";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly AgentOptions _options;
    private readonly ILogger<AgentUpgradeCoordinator> _logger;

    public AgentUpgradeCoordinator(IOptions<AgentOptions> options, ILogger<AgentUpgradeCoordinator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private string PendingPath => Path.Combine(_options.ExpandedDataDirectory, PendingFileName);

    private string ResultPath => Path.Combine(_options.ExpandedDataDirectory, ResultFileName);

    private string UpdatesRoot => Path.Combine(_options.ExpandedDataDirectory, UpdatesFolderName);

    private string StagedUpdaterDirectory => Path.Combine(_options.ExpandedDataDirectory, StagedUpdaterFolderName);

    /// <summary>
    /// 升级执行器所在路径。
    ///
    /// 它固定在安装目录的**同级**目录（…\BackupMonitor\Updater）：
    /// 放进安装目录的话，换文件时它自己也会被锁住，这条路就走不通了。
    /// 找不到它不是异常——现场可能是从老包装上来的机器，那种机器只能人工升级，
    /// 而说清楚这一点正是止损那一步要做的事。
    /// </summary>
    public string? ResolveUpdaterPath()
    {
        var configured = _options.UpdaterPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            return File.Exists(expanded) ? expanded : null;
        }

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(installDir);
        if (parent is null)
            return null;

        var candidate = Path.Combine(parent, "Updater", UpdaterFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    public async Task WritePendingAsync(PendingUpdate pending, CancellationToken ct)
    {
        await File.WriteAllTextAsync(PendingPath, JsonSerializer.Serialize(pending, JsonOptions), ct);
        _logger.LogInformation("升级包已暂存：版本 {Version}，目录 {Directory}", pending.Version, pending.PayloadDirectory);
    }

    public PendingUpdate? ReadPending()
    {
        try
        {
            if (!File.Exists(PendingPath))
                return null;

            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingPath), JsonOptions);
            if (pending is null || string.IsNullOrWhiteSpace(pending.PayloadDirectory))
                return null;

            return pending;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "读取暂存升级记录失败");
            return null;
        }
    }

    public void ClearPending() => TryDelete(PendingPath);

    /// <summary>
    /// 记一次安装尝试并立即落盘，返回递增后的次数。
    ///
    /// 必须在**拉起 updater 之前**调用：updater 一启动就会把本服务停掉，
    /// 之后这个进程再没有机会写下任何东西——写晚了等于没写。
    /// </summary>
    public int RecordAttempt(PendingUpdate pending)
    {
        pending.AttemptCount++;
        try
        {
            File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去就不计数。这会退化成旧的无限重试行为，但至少不挡住这次升级，
            // 而写不进 data 目录本身会由 AgentStateStore 那边报出来。
            _logger.LogWarning(ex, "升级尝试次数写盘失败，本次不计数");
        }

        return pending.AttemptCount;
    }

    public UpgradeResultRecord? ReadResult()
    {
        try
        {
            if (!File.Exists(ResultPath))
                return null;

            var record = JsonSerializer.Deserialize<UpgradeResultRecord>(File.ReadAllText(ResultPath), JsonOptions);
            return string.IsNullOrWhiteSpace(record?.Status) ? null : record;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "读取升级结果文件失败");
            return null;
        }
    }

    public void ClearResult() => TryDelete(ResultPath);

    /// <summary>
    /// 把升级执行器拉起来。返回 false 表示没拉起来（本机没有执行器，或启动失败）。
    ///
    /// 它起来之后做的第一件事就是停掉本服务，所以这个方法返回之后本进程
    /// 通常活不了几秒——**调用方不要在它后面安排任何还想跑完的收尾逻辑**。
    /// </summary>
    public bool TryLaunchUpdater(PendingUpdate pending)
    {
        var updaterPath = ResolveUpdaterPath();
        if (updaterPath is null)
        {
            _logger.LogWarning("本机没有安装升级执行器，暂存的升级包需要人工到这台机器上安装");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 工作目录刻意设成执行器自己的目录：设成安装目录的话，
                // 这个进程会一直占着那个目录的句柄，而它马上就要被改名。
                WorkingDirectory = Path.GetDirectoryName(updaterPath)!
            };
            startInfo.ArgumentList.Add("--install-dir");
            startInfo.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            startInfo.ArgumentList.Add("--data-dir");
            startInfo.ArgumentList.Add(_options.ExpandedDataDirectory);
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(pending.PayloadDirectory);
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add(pending.Version);
            if (pending.CommandId is not null)
            {
                startInfo.ArgumentList.Add("--command-id");
                startInfo.ArgumentList.Add(pending.CommandId.Value.ToString());
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogError("升级执行器没能启动");
                return false;
            }

            _logger.LogWarning(
                "升级执行器已启动（PID {Pid}），本服务即将被它停止以完成版本切换", process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动升级执行器失败");
            return false;
        }
    }

    /// <summary>
    /// 从升级包里把新版 updater 抢下来暂存（Agent 启动时调一次）。
    ///
    /// updater 住在安装目录的**同级**目录，而升级只换安装目录——所以它换不到自己，
    /// 每台机器上的 updater 会永久停在当初安装时的版本。对一个「以后都靠 OTA」的系统来说，
    /// 这个漂移会一直累积，直到某天真要改 updater，才发现全网的 updater 都是老的。
    ///
    /// 时机是关键：这里要赶在**正在跑的那个 updater 清理 payload 目录之前**把文件拿走。
    /// 窗口很宽——Agent 在服务启动后零点几秒就跑起来，而那边 WaitForHeartbeat 至少要等
    /// 一个心跳周期。而且这一步由 Agent 单方面完成，不需要 updater 配合，
    /// 所以老 updater 装的机器第一次升上来就能用。
    /// </summary>
    public void TryStageUpdaterFromPayload()
    {
        try
        {
            if (!Directory.Exists(UpdatesRoot))
                return;

            var installedVersion = ReadProductVersion(ResolveUpdaterPath());

            foreach (var payloadUpdaterDirectory in Directory.EnumerateDirectories(UpdatesRoot)
                         .Select(d => Path.Combine(d, "payload", UpdaterPayloadFolderName))
                         .Where(Directory.Exists))
            {
                var candidate = Path.Combine(payloadUpdaterDirectory, UpdaterFileName);
                var candidateVersion = ReadProductVersion(candidate);
                if (candidateVersion is null)
                    continue;

                if (installedVersion is not null && candidateVersion <= installedVersion)
                    continue;

                CopyFilesInto(payloadUpdaterDirectory, StagedUpdaterDirectory);
                _logger.LogInformation(
                    "已暂存新版升级执行器 {Candidate}（本机当前 {Installed}），待它退出后替换",
                    candidateVersion, installedVersion?.ToString() ?? "未知");
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "暂存新版升级执行器失败，本次跳过");
        }
    }

    /// <summary>
    /// 把暂存的新版 updater 换上去（升级循环每轮调一次）。
    ///
    /// **等它退出，不杀它**。新 Agent 起来的时候，那个 updater 正卡在 WaitForHeartbeat 里
    /// 等这台机器的心跳；它后面还要清备份目录、清暂存包，最后写 upgrade-result.json——
    /// 而那份文件是服务端唯一的成功凭据。杀掉它等于把「升级成功」这个结论一起杀掉，
    /// 服务端只能等超时，然后得出「这台机器没起来」这个与事实相反的结论。
    ///
    /// 换不成就把暂存留着下一轮重试，绝不留下换到一半的状态。
    /// </summary>
    public void TryInstallStagedUpdater()
    {
        try
        {
            var staged = Path.Combine(StagedUpdaterDirectory, UpdaterFileName);
            if (!File.Exists(staged))
                return;

            var installed = ResolveUpdaterPath();
            if (installed is null)
            {
                // 本机压根没装 updater（从老包装上来的机器）。这里不擅自建一个出来：
                // 那种机器本来就走人工升级，凭空多出一个 updater 只会让现场更难判断。
                TryDeleteDirectory(StagedUpdaterDirectory);
                return;
            }

            var stagedVersion = ReadProductVersion(staged);
            var installedVersion = ReadProductVersion(installed);
            if (stagedVersion is null || (installedVersion is not null && stagedVersion <= installedVersion))
            {
                TryDeleteDirectory(StagedUpdaterDirectory);
                return;
            }

            if (IsUpdaterRunning())
                return;

            var targetDirectory = Path.GetDirectoryName(installed)!;
            foreach (var file in Directory.EnumerateFiles(StagedUpdaterDirectory, "*", SearchOption.TopDirectoryOnly))
                ReplaceFile(file, Path.Combine(targetDirectory, Path.GetFileName(file)));

            _logger.LogInformation(
                "升级执行器已更新：{Installed} → {Staged}",
                installedVersion?.ToString() ?? "未知", stagedVersion);
            TryDeleteDirectory(StagedUpdaterDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "替换升级执行器失败，保留暂存下一轮重试");
        }
    }

    /// <summary>
    /// 本机 updater 的版本，随心跳上报给服务端；没装 updater 时返回 null。
    ///
    /// 服务端拿它回答「哪几台的 updater 还是老的」——在自更新机制上线之前，
    /// 这个问题只能挨台机器远程上去看文件属性。
    /// </summary>
    public string? GetInstalledUpdaterVersion() => ReadProductVersion(ResolveUpdaterPath())?.ToString();

    /// <summary>updater 是否还在跑。判不出来时一律当作「在跑」——宁可晚换一轮，也不要换到一半。</summary>
    private static bool IsUpdaterRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(UpdaterProcessName);
        }
        catch (Exception)
        {
            return true;
        }

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>产品版本号；读不到或解析不出来返回 null。构建后缀（1.3.2+abc123）在这里截掉。</summary>
    private static Version? ReadProductVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var raw = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            var core = raw?.Split('+')[0].Trim();
            return Version.TryParse(core, out var version) ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>先写同目录临时文件再整体改名：断电时要么是旧的要么是新的，不会是半个。</summary>
    private static void ReplaceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".new";
        File.Copy(source, temporary, overwrite: true);
        File.Move(temporary, destination, overwrite: true);
    }

    private static void CopyFilesInto(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "删除目录失败 {Path}", path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "删除文件失败 {Path}", path);
        }
    }
}
