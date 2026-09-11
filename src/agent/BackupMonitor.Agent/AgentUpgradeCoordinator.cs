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
