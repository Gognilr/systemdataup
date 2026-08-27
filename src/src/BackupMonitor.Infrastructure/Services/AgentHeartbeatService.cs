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
    private const int MaxDisks = 64;
    private const int MaxServiceStates = 128;
    private const int MaxUserSessions = 128;
    private readonly AppDbContext _db;
    private readonly SystemSettingsProvider _settings;
    private readonly IAlertingService _alerting;
    private readonly IAgentNotificationService _agentNotifications;
    private readonly IAgentConfigService _configService;
    private readonly ILogger<AgentHeartbeatService> _logger;

    public AgentHeartbeatService(
        AppDbContext db,
        SystemSettingsProvider settings,
        IAlertingService alerting,
        IAgentNotificationService agentNotifications,
        IAgentConfigService configService,
        ILogger<AgentHeartbeatService> logger)
    {
        _db = db;
        _settings = settings;
        _alerting = alerting;
        _agentNotifications = agentNotifications;
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

        // 心跳是一个全量快照：明细、客户端状态、磁盘、服务状态和用户会话必须原子提交，
        // 否则 ExecuteDelete 成功后后续插入失败会留下半个快照。
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var committed = false;
        var serviceAlerts = new List<(string AlertKey, bool Raise, string Title, string Message)>();
        try
        {
            var now = DateTime.UtcNow;

        // 1. 心跳明细入库；分区由 PartitionMaintenanceWorker 提前维护，RetentionCleanupWorker 负责历史清理。
        _db.ClientHeartbeats.Add(new ClientHeartbeat
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ReceivedAt = now,
            ClientTime = request.ClientTime,
            AgentUptimeSeconds = request.AgentUptimeSeconds,
            SystemUptimeSeconds = request.SystemUptimeSeconds,
            CpuPercent = request.Metrics?.CpuPercent,
            AgentCpuPercent = request.Metrics?.AgentCpuPercent,
            MemoryPercent = request.Metrics?.MemoryPercent,
            MemoryTotalBytes = request.Metrics?.MemoryTotalBytes,
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

        // 3. 磁盘按业务键更新，并删除本次没有上报的磁盘，避免每次心跳全删全插。
        var disks = Limit(request.Disks, MaxDisks, "Disks", client.Id);
        if (disks is not null)
            await UpsertDisksAsync(client.Id, disks, now, ct);

        // 4. 关键服务状态采样与告警
        var serviceStates = Limit(request.ServiceStates, MaxServiceStates, "ServiceStates", client.Id);
        if (serviceStates is { Count: > 0 })
        {
            var definitions = await _db.MonitoredServiceDefinitions
                .Where(d => d.ClientId == client.Id && d.Enabled)
                .ToListAsync(ct);
            var reportedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var state in serviceStates)
            {
                if (string.IsNullOrWhiteSpace(state.ServiceName)
                    || !reportedServices.Add(state.ServiceName.Trim()))
                    continue;

                var definition = definitions.FirstOrDefault(d =>
                    string.Equals(d.ServiceName, state.ServiceName, StringComparison.OrdinalIgnoreCase));
                if (definition is null)
                    continue;

                if (!EnumMapping.TryParseSnakeCase<ServiceActualState>(state.ActualState, out var actualState))
                    continue;

                var startType = EnumMapping.TryParseSnakeCase<ServiceStartType>(state.StartType ?? string.Empty, out var st)
                    ? st
                    : ServiceStartType.Auto;
                if (definition.CurrentActualState != actualState
                    || definition.CurrentStartType != startType)
                {
                    _db.ClientServiceStates.Add(new ClientServiceState
                    {
                        Id = Guid.NewGuid(),
                        DefinitionId = definition.Id,
                        ClientId = client.Id,
                        ActualState = actualState,
                        StartType = startType,
                        SampledAt = now
                    });
                }

                definition.CurrentActualState = actualState;
                definition.CurrentStartType = startType;
                definition.CurrentSampledAt = now;

                var alertKey = $"client:{client.Id}:service:{definition.Id}";
                var mismatch = definition.ExpectedState == ServiceExpectedState.Running
                    ? actualState != ServiceActualState.Running
                    : actualState == ServiceActualState.Running;

                serviceAlerts.Add((
                    alertKey,
                    definition.AlertOnMismatch && mismatch,
                    $"关键服务 {definition.DisplayName} 状态异常",
                    $"期望 {EnumMapping.ToSnakeCase(definition.ExpectedState)}，实际 {EnumMapping.ToSnakeCase(actualState)}"));
            }
        }

        // 5. 当前 Windows 用户会话按业务键更新，并删除本次没有上报的会话。
        var userSessions = Limit(request.UserSessions, MaxUserSessions, "UserSessions", client.Id);
        if (userSessions is not null)
            await UpsertUserSessionsAsync(client.Id, userSessions, now, ct);

        await _db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        committed = true;

        // 告警/通知是旁路副作用，放在快照事务提交后，避免告警去重冲突使心跳事务进入 aborted 状态。

        // 心跳到达即证明客户端还活着：无条件恢复 SystemWatchdogWorker 可能挂起的离线告警。
        // 不判断「刚才是不是 Offline」——巡检把状态改成 Offline 与心跳落库之间存在竞态，
        // 恢复一条本就不存在的告警是幂等空操作，漏恢复一条却会让告警中心留下永久红点。
        await _alerting.RecoverAsync($"client:{client.Id}:offline", ct);

        foreach (var alert in serviceAlerts)
        {
            if (alert.Raise)
            {
                await _alerting.RaiseAsync(
                    alert.AlertKey,
                    AlertLevel.Warning,
                    "service_state",
                    alert.Title,
                    alert.Message,
                    clientId: client.Id,
                    ct: ct);
            }
            else
            {
                await _alerting.RecoverAsync(alert.AlertKey, ct);
            }
        }

        // 6. CPU、内存和源磁盘阈值告警；告警键固定，连续心跳只聚合不刷屏。
        await EvaluateResourceAlertsAsync(client.Id, request, disks, ct);
        await EvaluateCertificateAlertAsync(client, now, ct);

        // 7. 心跳响应
        var requiredVersion = await _configService.GetRequiredVersionAsync(client.Id, ct);
        var commandsAvailable = await _db.Commands
            .AnyAsync(c => c.ClientId == client.Id && c.Status == CommandStatus.Pending && c.ExpiresAt > now, ct);
        var heartbeatInterval = await _settings.GetIntAsync("heartbeat_interval_seconds", 60, ct);
        var notifications = await _agentNotifications.ClaimForHeartbeatAsync(
            client.Id,
            now.AddHours(-24),
            now,
            20,
            ct);

            var response = new Shared.Models.Agent.HeartbeatResponse
            {
                ServerTime = now,
                RequiredConfigVersion = requiredVersion,
                CommandsAvailable = commandsAvailable,
                HeartbeatIntervalSeconds = heartbeatInterval,
                CertificateRenewalRequired = client.CertificateExpiresAt is not null
                    && client.CertificateExpiresAt <= now.AddDays(30),
                Notifications = notifications.ToList()
            };

            return response;
        }
        catch
        {
            if (!committed)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task EvaluateResourceAlertsAsync(
        Guid clientId,
        Shared.Models.Agent.HeartbeatRequest request,
        IReadOnlyList<Shared.Models.Agent.HeartbeatDiskDto>? disks,
        CancellationToken ct)
    {
        var cpuThreshold = Math.Clamp(await _settings.GetIntAsync("client_cpu_alert_percent", 85, ct), 1, 100);
        var memoryThreshold = Math.Clamp(await _settings.GetIntAsync("client_memory_alert_percent", 90, ct), 1, 100);
        var diskFreeThreshold = Math.Clamp(await _settings.GetIntAsync("client_disk_free_alert_percent", 10, ct), 1, 99);

        var metrics = request.Metrics;
        if (metrics?.CpuPercent is not null)
        {
            var alertKey = $"client:{clientId}:resource:cpu";
            if (metrics.CpuPercent >= cpuThreshold)
            {
                await _alerting.RaiseAsync(
                    alertKey,
                    AlertLevel.Warning,
                    "client_resource",
                    "客户端 CPU 使用率过高",
                    $"当前 {metrics.CpuPercent:0.##}%（阈值 {cpuThreshold}%）",
                    clientId: clientId,
                    ct: ct);
            }
            else
            {
                await _alerting.RecoverAsync(alertKey, ct);
            }
        }

        if (metrics?.MemoryPercent is not null)
        {
            var alertKey = $"client:{clientId}:resource:memory";
            if (metrics.MemoryPercent >= memoryThreshold)
            {
                await _alerting.RaiseAsync(
                    alertKey,
                    AlertLevel.Warning,
                    "client_resource",
                    "客户端内存使用率过高",
                    $"当前 {metrics.MemoryPercent:0.##}%（阈值 {memoryThreshold}%）",
                    clientId: clientId,
                    ct: ct);
            }
            else
            {
                await _alerting.RecoverAsync(alertKey, ct);
            }
        }

        // 审计 B-09：磁盘快照没变时 Agent 不再重复上报（disks 为 null），
        // 但「快照没变」恰恰意味着盘还是那么满——原先这一整段直接被跳过，
        // 磁盘告警会因为「没有新数据」而不再评估，也就再不会恢复。
        // 回落到库里最近一次上报的 ClientDisks，判定依据不变。
        var evaluated = disks?
            .Select(d => (d.DriveName, d.IsSourceVolume, d.TotalBytes, d.FreeBytes))
            .ToList()
            ?? await _db.ClientDisks
                .AsNoTracking()
                .Where(d => d.ClientId == clientId)
                .Select(d => new ValueTuple<string, bool, long?, long?>(
                    d.DriveName, d.IsSourceVolume, d.TotalBytes, d.FreeBytes))
                .ToListAsync(ct);

        foreach (var (driveName, isSourceVolume, totalBytes, freeBytes) in evaluated)
        {
            if (!isSourceVolume || totalBytes is not > 0 || freeBytes is null)
                continue;

            var freePercent = (decimal)freeBytes.Value / totalBytes.Value * 100;
            var alertKey = $"client:{clientId}:resource:disk:{driveName}";
            if (freePercent <= diskFreeThreshold)
            {
                await _alerting.RaiseAsync(
                    alertKey,
                    AlertLevel.Warning,
                    "client_resource",
                    $"客户端源磁盘 {driveName} 空间不足",
                    $"可用 {freePercent:0.##}%（阈值 {diskFreeThreshold}%）",
                    clientId: clientId,
                    ct: ct);
            }
            else
            {
                await _alerting.RecoverAsync(alertKey, ct);
            }
        }
    }

    private async Task EvaluateCertificateAlertAsync(Client client, DateTime now, CancellationToken ct)
    {
        var alertKey = $"client:{client.Id}:certificate-expiry";
        if (client.CertificateExpiresAt is not null
            && client.CertificateExpiresAt <= now.AddDays(30))
        {
            var remainingDays = Math.Max(0, (int)Math.Ceiling((client.CertificateExpiresAt.Value - now).TotalDays));
            await _alerting.RaiseAsync(
                alertKey,
                remainingDays <= 7 ? AlertLevel.Critical : AlertLevel.Warning,
                "certificate_expiry",
                "客户端证书即将到期",
                $"证书剩余 {remainingDays} 天，Agent 将自动尝试续签。",
                clientId: client.Id,
                ct: ct);
        }
        else
        {
            await _alerting.RecoverAsync(alertKey, ct);
        }
    }

    private async Task UpsertDisksAsync(
        Guid clientId,
        IReadOnlyList<Shared.Models.Agent.HeartbeatDiskDto> reports,
        DateTime sampledAt,
        CancellationToken ct)
    {
        var existing = await _db.ClientDisks
            .Where(d => d.ClientId == clientId)
            .ToListAsync(ct);
        var byDrive = existing
            .GroupBy(d => d.DriveName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var reportedDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var report in reports)
        {
            var driveName = report.DriveName?.Trim();
            if (string.IsNullOrWhiteSpace(driveName) || driveName.Length > 32 || !reportedDrives.Add(driveName))
                continue;

            if (!byDrive.TryGetValue(driveName, out var disk))
            {
                disk = new ClientDisk { Id = Guid.NewGuid(), ClientId = clientId, DriveName = driveName };
                _db.ClientDisks.Add(disk);
            }

            disk.VolumeLabel = TrimTo(report.VolumeLabel, 128);
            disk.Filesystem = TrimTo(report.Filesystem, 32);
            disk.TotalBytes = report.TotalBytes;
            disk.FreeBytes = report.FreeBytes;
            disk.IsSourceVolume = report.IsSourceVolume;
            disk.SampledAt = sampledAt;
        }

        foreach (var disk in existing.Where(d => !reportedDrives.Contains(d.DriveName)))
            _db.ClientDisks.Remove(disk);
    }

    private async Task UpsertUserSessionsAsync(
        Guid clientId,
        IReadOnlyList<Shared.Models.Agent.HeartbeatUserSessionDto> reports,
        DateTime sampledAt,
        CancellationToken ct)
    {
        var existing = await _db.ClientUserSessions
            .Where(s => s.ClientId == clientId)
            .ToListAsync(ct);
        var bySession = existing
            .GroupBy(s => s.SessionId)
            .ToDictionary(g => g.Key, g => g.First());
        var reportedSessions = new HashSet<int>();

        foreach (var report in reports)
        {
            if (string.IsNullOrWhiteSpace(report.State) || !reportedSessions.Add(report.SessionId))
                continue;

            if (!bySession.TryGetValue(report.SessionId, out var session))
            {
                session = new ClientUserSession
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    SessionId = report.SessionId
                };
                _db.ClientUserSessions.Add(session);
            }

            session.Username = TrimTo(report.Username, 255);
            session.Domain = TrimTo(report.Domain, 255);
            session.State = TrimTo(report.State, 32) ?? "unknown";
            session.ClientName = TrimTo(report.ClientName, 255);
            session.ClientAddress = TrimTo(report.ClientAddress, 128);
            session.IsRemote = report.IsRemote;
            session.LogonAt = report.LogonAt;
            session.SampledAt = sampledAt;
        }

        foreach (var session in existing.Where(s => !reportedSessions.Contains(s.SessionId)))
            _db.ClientUserSessions.Remove(session);
    }

    private IReadOnlyList<T>? Limit<T>(IReadOnlyList<T>? values, int max, string field, Guid clientId)
    {
        if (values is null || values.Count <= max)
            return values;

        _logger.LogWarning(
            "客户端 {ClientId} 心跳字段 {Field} 超过上限 {Max}，已截断 {Actual} 条",
            clientId,
            field,
            max,
            values.Count);
        return values.Take(max).ToList();
    }

    private static string? TrimTo(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
