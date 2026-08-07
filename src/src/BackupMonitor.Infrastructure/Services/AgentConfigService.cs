using System.Text.Json;
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

    public async Task<long> GetRequiredVersionAsync(Guid clientId, CancellationToken ct = default)
    {
        var max = await _db.BackupTasks
            .Where(t => t.ClientId == clientId)
            .Select(t => (long?)t.ConfigVersion)
            .MaxAsync(ct);
        return max ?? 0;
    }

    public async Task<AgentConfigResponse> GetConfigAsync(Guid clientId, long currentVersion, CancellationToken ct = default)
    {
        var tasks = await _db.BackupTasks
            .Where(t => t.ClientId == clientId)
            .OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

        var services = await _db.MonitoredServiceDefinitions
            .Where(d => d.ClientId == clientId && d.Enabled)
            .OrderBy(d => d.ServiceName)
            .ToListAsync(ct);

        var version = tasks.Count > 0 ? tasks.Max(t => t.ConfigVersion) : 0;

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
                ScanSchedule = t.ScanSchedule,
                ScheduleTimezone = t.ScheduleTimezone,
                StabilityIntervalSeconds = t.StabilityIntervalSeconds,
                MaxStabilityWaitSeconds = t.MaxStabilityWaitSeconds,
                BandwidthLimitKbps = t.BandwidthLimitKbps,
                ChunkSizeBytes = t.ChunkSizeBytes,
                RecognizerConfig = t.RecognizerConfig,
                ConfigVersion = t.ConfigVersion
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

        // 配置签名（防篡改）
        var canonical = JsonSerializer.Serialize(new
        {
            response.Version,
            tasks = response.Tasks,
            services = response.MonitoredServices,
            global = response.GlobalSettings
        });
        response.Signature = _signer.SignConfig(response.Version, clientId, canonical);

        return response;
    }
}
