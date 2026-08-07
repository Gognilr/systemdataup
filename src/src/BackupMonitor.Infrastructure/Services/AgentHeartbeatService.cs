using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>Agent 心跳服务（设计书 11.1）</summary>
public interface IAgentHeartbeatService
{
    Task<Shared.Models.Agent.HeartbeatResponse> ProcessAsync(
        Guid clientId, Shared.Models.Agent.HeartbeatRequest request, string? agentVersion, CancellationToken ct = default);
}

/// <summary>Agent 心跳实现</summary>
public class AgentHeartbeatService : IAgentHeartbeatService
{
    private readonly AppDbContext _db;
    private readonly SystemSettingsProvider _settings;
    private readonly IAlertingService _alerting;
    private readonly IAgentConfigService _configService;
    private readonly ILogger<AgentHeartbeatService> _logger;

    public AgentHeartbeatService(
        AppDbContext db,
        SystemSettingsProvider settings,
        IAlertingService alerting,
        IAgentConfigService configService,
        ILogger<AgentHeartbeatService> logger)
    {
        _db = db;
        _settings = settings;
        _alerting = alerting;
        _configService = configService;
        _logger = logger;
    }

    public async Task<Shared.Models.Agent.HeartbeatResponse> ProcessAsync(
        Guid clientId, Shared.Models.Agent.HeartbeatRequest request, string? agentVersion, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new Shared.Exceptions.BusinessException("CLIENT_NOT_REGISTERED", "客户端未注册", 401);

        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new Shared.Exceptions.BusinessException("CLIENT_DISABLED", "客户端已禁用或已注销", 403);

        var now = DateTime.UtcNow;

        // 1. 心跳明细入库（分区表，分区缺失时由数据库维护任务兜底）
        _db.ClientHeartbeats.Add(new ClientHeartbeat
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ReceivedAt = now,
            ClientTime = request.ClientTime,
            AgentUptimeSeconds = request.AgentUptimeSeconds,
            SystemUptimeSeconds = request.SystemUptimeSeconds,
            CpuPercent = request.Metrics?.CpuPercent,
            MemoryPercent = request.Metrics?.MemoryPercent,
            MemoryAvailableBytes = request.Metrics?.MemoryAvailableBytes,
            AgentMemoryBytes = request.Metrics?.AgentMemoryBytes,
            NetworkSendBps = request.Metrics?.NetworkSendBps,
            NetworkReceiveBps = request.Metrics?.NetworkReceiveBps,
            ActiveCommandCount = request.ActiveCommands?.Count,
            ActiveUploadCount = request.ActiveUploads?.Count
        });

        // 2. 客户端状态更新
        client.LastHeartbeatAt = now;
        if (!string.IsNullOrWhiteSpace(agentVersion))
            client.AgentVersion = agentVersion;
        if (request.ClientTime is not null)
            client.TimeOffsetSeconds = (int)Math.Clamp((now - request.ClientTime.Value.ToUniversalTime()).TotalSeconds, int.MinValue, int.MaxValue);
        if (client.Status is ClientStatus.Offline or ClientStatus.SuspectedOffline or ClientStatus.CertificateExpired)
        {
            client.Status = client.CertificateExpiresAt is not null && client.CertificateExpiresAt <= now
                ? ClientStatus.CertificateExpired
                : ClientStatus.Online;
        }

        // 3. 磁盘状态全量替换
        if (request.Disks is { Count: > 0 })
        {
            await _db.ClientDisks.Where(d => d.ClientId == client.Id).ExecuteDeleteAsync(ct);
            foreach (var disk in request.Disks)
            {
                _db.ClientDisks.Add(new ClientDisk
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    DriveName = disk.DriveName,
                    VolumeLabel = disk.VolumeLabel,
                    Filesystem = disk.Filesystem,
                    TotalBytes = disk.TotalBytes,
                    FreeBytes = disk.FreeBytes,
                    IsSourceVolume = disk.IsSourceVolume,
                    SampledAt = now
                });
            }
        }

        // 4. 关键服务状态采样与告警
        if (request.ServiceStates is { Count: > 0 })
        {
            var definitions = await _db.MonitoredServiceDefinitions
                .Where(d => d.ClientId == client.Id && d.Enabled)
                .ToListAsync(ct);

            foreach (var state in request.ServiceStates)
            {
                var definition = definitions.FirstOrDefault(d =>
                    string.Equals(d.ServiceName, state.ServiceName, StringComparison.OrdinalIgnoreCase));
                if (definition is null)
                    continue;

                if (!EnumMapping.TryParseSnakeCase<ServiceActualState>(state.ActualState, out var actualState))
                    continue;

                _db.ClientServiceStates.Add(new ClientServiceState
                {
                    Id = Guid.NewGuid(),
                    DefinitionId = definition.Id,
                    ClientId = client.Id,
                    ActualState = actualState,
                    StartType = EnumMapping.TryParseSnakeCase<ServiceStartType>(state.StartType ?? string.Empty, out var st)
                        ? st
                        : ServiceStartType.Auto,
                    SampledAt = now
                });

                var alertKey = $"client:{client.Id}:service:{definition.Id}";
                var mismatch = definition.ExpectedState == ServiceExpectedState.Running
                    ? actualState != ServiceActualState.Running
                    : actualState == ServiceActualState.Running;

                if (definition.AlertOnMismatch && mismatch)
                {
                    await _alerting.RaiseAsync(
                        alertKey,
                        AlertLevel.Warning,
                        "service_state",
                        $"关键服务 {definition.DisplayName} 状态异常",
                        $"期望 {EnumMapping.ToSnakeCase(definition.ExpectedState)}，实际 {EnumMapping.ToSnakeCase(actualState)}",
                        clientId: client.Id,
                        ct: ct);
                }
                else
                {
                    await _alerting.RecoverAsync(alertKey, ct);
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        // 5. 心跳响应
        var requiredVersion = await _configService.GetRequiredVersionAsync(client.Id, ct);
        var commandsAvailable = await _db.Commands
            .AnyAsync(c => c.ClientId == client.Id && c.Status == CommandStatus.Pending && c.ExpiresAt > now, ct);
        var heartbeatInterval = await _settings.GetIntAsync("heartbeat_interval_seconds", 60, ct);

        return new Shared.Models.Agent.HeartbeatResponse
        {
            ServerTime = now,
            RequiredConfigVersion = requiredVersion,
            CommandsAvailable = commandsAvailable,
            HeartbeatIntervalSeconds = heartbeatInterval
        };
    }
}
