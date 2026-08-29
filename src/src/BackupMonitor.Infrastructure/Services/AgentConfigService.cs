using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>Agent 配置同步服务（设计书 11.2）</summary>
public interface IAgentConfigService
{
    /// <summary>客户端当前应持有的最新配置版本（任务表 config_version 最大值）</summary>
    Task<long> GetRequiredVersionAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>生成完整下发配置</summary>
    Task<AgentConfigResponse> GetConfigAsync(Guid clientId, long currentVersion, CancellationToken ct = default);
}

/// <summary>Agent 配置同步实现</summary>
public class AgentConfigService : IAgentConfigService
{
    private readonly AppDbContext _db;
    private readonly SystemSettingsProvider _settings;
    private readonly CommandSigner _signer;

    public AgentConfigService(AppDbContext db, SystemSettingsProvider settings, CommandSigner signer)
    {
        _db = db;
        _settings = settings;
        _signer = signer;
    }

    public async Task<long> GetRequiredVersionAsync(Guid clientId, CancellationToken ct = default) =>
        await ComputeVersionAsync(clientId, ct);

    /// <summary>
    /// 配置版本 = 任务版本最大值 与 客户端配置修订号 的较大者。
    ///
    /// 只看任务版本的话，监控服务定义的变更推不动版本号，Agent 就不会重新拉配置——
    /// 新增的关键服务永远到不了客户端，且没有任何报错；客户端一个任务都没有时
    /// 版本恒为 0，问题更彻底。两个数都是单调递增的，取 max 仍然单调。
    /// </summary>
    private async Task<long> ComputeVersionAsync(Guid clientId, CancellationToken ct)
    {
        var taskVersion = await _db.BackupTasks
            .Where(t => t.ClientId == clientId)
            .Select(t => (long?)t.ConfigVersion)
            .MaxAsync(ct) ?? 0;

        var revision = await _db.Clients
            .Where(c => c.Id == clientId)
            .Select(c => (long?)c.ConfigRevision)
            .FirstOrDefaultAsync(ct) ?? 0;

        return Math.Max(taskVersion, revision);
    }

    public async Task<AgentConfigResponse> GetConfigAsync(Guid clientId, long currentVersion, CancellationToken ct = default)
    {
        var tasks = await _db.BackupTasks
            .Where(t => t.ClientId == clientId)
            .OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

        // 进了备份计划的任务由服务端计划驱动，它自己的扫描计划不再下发——
        // 两者都留着的话，同一个任务一天会跑两次（一次 Agent 按 cron 触发，一次计划驱动）。
        var plannedTaskIds = await _db.BackupPlanItems
            .Where(i => i.Task.ClientId == clientId)
            .Select(i => i.TaskId)
            .ToListAsync(ct);

        var services = await _db.MonitoredServiceDefinitions
            .Where(d => d.ClientId == clientId && d.Enabled)
            .OrderBy(d => d.ServiceName)
            .ToListAsync(ct);

        // 与 GetRequiredVersionAsync 必须用同一个算法：这里算小了，Agent 存下的版本
        // 会永远低于心跳要求的版本，于是每一次心跳都触发一次全量配置拉取。
        var version = await ComputeVersionAsync(clientId, ct);

        var fingerprints = await LoadQuickFingerprintsAsync(tasks.Select(t => t.Id).ToList(), ct);

        var response = new AgentConfigResponse
        {
            Version = version,
            IssuedAt = DateTime.UtcNow,
            Tasks = tasks.Select(t => new AgentTaskConfigDto
            {
                TaskId = t.Id,
                Name = t.Name,
                ApplicationName = t.ApplicationName,
                SourcePath = t.SourcePath,
                RecognizerType = EnumMapping.ToSnakeCase(t.RecognizerType),
                TaskMode = EnumMapping.ToSnakeCase(t.TaskMode),
                Enabled = t.Enabled,
                ScanSchedule = plannedTaskIds.Contains(t.Id) ? null : t.ScanSchedule,
                ScheduleTimezone = t.ScheduleTimezone,
                StabilityIntervalSeconds = t.StabilityIntervalSeconds,
                MaxStabilityWaitSeconds = t.MaxStabilityWaitSeconds,
                BandwidthLimitKbps = t.BandwidthLimitKbps,
                ChunkSizeBytes = t.ChunkSizeBytes,
                RandomDelayMinutes = t.RandomDelayMinutes,
                RecognizerConfig = t.RecognizerConfig,
                ConfigVersion = t.ConfigVersion,
                LastQuickFingerprints = fingerprints.TryGetValue(t.Id, out var f) ? f : []
            }).ToList(),
            MonitoredServices = services.Select(d => new AgentMonitoredServiceDto
            {
                ServiceName = d.ServiceName,
                DisplayName = d.DisplayName,
                ExpectedState = EnumMapping.ToSnakeCase(d.ExpectedState),
                AlertOnMismatch = d.AlertOnMismatch
            }).ToList(),
            GlobalSettings = new AgentGlobalSettingsDto
            {
                HeartbeatIntervalSeconds = await _settings.GetIntAsync("heartbeat_interval_seconds", 60, ct),
                MaxConcurrentUploads = await _settings.GetIntAsync("max_concurrent_uploads", 2, ct)
            }
        };

        // 配置签名（防篡改）；规范化逻辑与 Agent 共用，避免两端 JSON 漂移。
        response.Signature = _signer.SignConfig(response, clientId);

        return response;
    }

    /// <summary>
    /// 每个任务 / 业务单元最近一次候选备份集的 QuickFingerprint，供 Agent 的两段式扫描当基线。
    ///
    /// 取「最近一次预检」的那一条而不是「最近一次入库」的：基线要回答的是
    /// 「磁盘上这份和 Agent 上次看到的那份是不是同一个」，与它有没有传上来无关。
    ///
    /// 没有业务单元的候选归到 root，与 BackupScanner 拼 candidateKey 时的
    /// `businessUnit?.ExternalKey ?? "root"` 是同一个口径——两边对不上的话，
    /// 基线永远命中不了，两段式退化成每次都算全量哈希（慢，但不会答错）。
    /// </summary>
    private async Task<Dictionary<Guid, List<AgentUnitFingerprintDto>>> LoadQuickFingerprintsAsync(
        List<Guid> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0)
            return [];

        var rows = await _db.CandidateBackupSets.AsNoTracking()
            .Where(c => taskIds.Contains(c.TaskId) && c.QuickFingerprint != null)
            .Select(c => new
            {
                c.TaskId,
                ExternalKey = c.BusinessUnit == null ? null : c.BusinessUnit.ExternalKey,
                c.QuickFingerprint,
                c.PrecheckedAt,
                c.UpdatedAt
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.TaskId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.ExternalKey) ? "root" : r.ExternalKey!)
                    .Select(unit => new AgentUnitFingerprintDto
                    {
                        ExternalKey = unit.Key,
                        QuickFingerprint = unit
                            .OrderByDescending(r => r.PrecheckedAt ?? r.UpdatedAt)
                            .ThenByDescending(r => r.UpdatedAt)
                            .First().QuickFingerprint!
                    })
                    .OrderBy(f => f.ExternalKey, StringComparer.Ordinal)
                    .ToList());
    }
}
