using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// Agent 版本漂移（整改清单 2026-09-10 · R11）。
///
/// 升级此前只有一个手动批量下发接口，界面上没有任何地方回答
/// 「哪些机器还在旧版本」。于是一台两年没升过级的 Agent 和一台昨天刚装的
/// 在客户端列表上长得一模一样——直到某个只在新版本里修好的缺陷在它身上复现。
/// </summary>
public interface IAgentVersionService
{
    Task<AgentVersionStatusDto> GetStatusAsync(CancellationToken ct = default);
}

public class AgentVersionService : IAgentVersionService
{
    /// <summary>随附 Agent 版本的 system_settings 覆盖键（留给现场手工指定）。</summary>
    public const string BundledVersionKey = "bundled_agent_version";

    /// <summary>落后多少天开始告警的 system_settings 键。</summary>
    public const string DriftAlertDaysKey = "agent_version_drift_alert_days";

    /// <summary>
    /// 默认 30 天。这个数不是「多久该升级」，而是「多久还没升级就该有人被提醒一下」——
    /// 局域网现场的升级往往要等一个维护窗口，判紧了只会造一批没人处理的告警。
    /// </summary>
    public const int DefaultDriftAlertDays = 30;

    /// <summary>随附版本号文件，由 build-turnkey.ps1 从实际发布出来的 exe 上取值写出。</summary>
    private const string VersionFileRelativePath = "wwwroot/downloads/agent-version.txt";

    private readonly AppDbContext _db;
    private readonly SystemSettingsProvider _settings;
    private readonly ILogger<AgentVersionService> _logger;

    public AgentVersionService(
        AppDbContext db,
        SystemSettingsProvider settings,
        ILogger<AgentVersionService> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public async Task<AgentVersionStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var (bundled, availableSince) = await ResolveBundledVersionAsync(ct);
        var alertDays = Math.Clamp(
            await _settings.GetIntAsync(DriftAlertDaysKey, DefaultDriftAlertDays, ct), 1, 3650);

        var result = new AgentVersionStatusDto
        {
            BundledVersion = bundled,
            BundledAvailableSince = availableSince,
            DriftAlertDays = alertDays
        };

        if (bundled is null || !TryParse(bundled, out var bundledVersion))
            return result;   // 不知道随附版本就不判漂移，宁可什么都不说也不要说错

        // 只看在册的机器：待审批 / 已注销的不参与判定，它们本来就不在服役。
        var clients = await _db.Clients.AsNoTracking()
            .Where(c => c.Status == ClientStatus.Online
                        || c.Status == ClientStatus.SuspectedOffline
                        || c.Status == ClientStatus.Offline)
            .Select(c => new { c.Id, c.Hostname, c.DisplayName, c.AgentVersion, c.UpdaterVersion })
            .ToListAsync(ct);

        foreach (var client in clients)
        {
            // 版本号读不出来（老版本 Agent 从没上报过）同样算落后：
            // 「不知道它是哪一版」和「知道它是旧版」在处置上是同一件事——都要去看一眼。
            var known = TryParse(client.AgentVersion, out var current);
            var agentOutdated = !known || current < bundledVersion;

            // updater 单独判一次：它住在安装目录之外，升级换不到它自己，所以完全可能
            // 「Agent 已经是最新的，updater 还停在两年前」——而 updater 侧的修复
            // 在那台机器上就是不生效的，界面上却一点看不出来。
            //
            // 只在**确实读到了一个更低的版本号**时才算落后。读不到不算：
            // 那既可能是没装 updater（老包装上来的机器，本来就走人工升级），
            // 也可能是还没升到会上报版本号的 1.3.2。这两种都不是能靠告警催出来的事，
            // 报了也只会变成一条谁都清不掉的告警。
            var updaterOutdated = TryParse(client.UpdaterVersion, out var updater)
                                  && updater < bundledVersion;

            if (!agentOutdated && !updaterOutdated)
                continue;

            result.OutdatedClients.Add(new AgentVersionDriftItemDto
            {
                ClientId = client.Id,
                Hostname = client.Hostname,
                DisplayName = client.DisplayName,
                AgentVersion = known ? client.AgentVersion : null,
                UpdaterVersion = client.UpdaterVersion
            });
        }

        result.Overdue = result.OutdatedClients.Count > 0
                         && availableSince is not null
                         && (DateTime.UtcNow - availableSince.Value).TotalDays > alertDays;
        return result;
    }

    /// <summary>
    /// 随附版本与「它从什么时候起可用」。
    ///
    /// 优先 system_settings 的显式覆盖（现场可能手工放了一个别的包）；
    /// 否则读发布目录里的版本号文件，「可用起始时刻」取该文件的最后写入时间——
    /// 那正是这一版被装到这台服务器上的时刻，比任何自己维护的字段都准。
    /// </summary>
    private async Task<(string? Version, DateTime? AvailableSince)> ResolveBundledVersionAsync(CancellationToken ct)
    {
        var configured = await _settings.GetStringAsync(BundledVersionKey, ct);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var row = await _db.SystemSettings.AsNoTracking()
                .Where(s => s.SettingKey == BundledVersionKey)
                .Select(s => (DateTime?)s.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            return (configured.Trim(), row);
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, VersionFileRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return (null, null);

            var text = (await File.ReadAllTextAsync(path, ct)).Trim();
            return string.IsNullOrWhiteSpace(text)
                ? (null, null)
                : (text, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取随附 Agent 版本号文件失败");
            return (null, null);
        }
    }

    /// <summary>
    /// 版本号解析。带后缀的信息版本号（1.1.0+abc123）在这里很常见，
    /// 取第一段再解析——解析不出来一律当「不知道」，不猜。
    /// </summary>
    private static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var core = value.Split('+', '-')[0].Trim();
        return Version.TryParse(core, out version!);
    }
}
