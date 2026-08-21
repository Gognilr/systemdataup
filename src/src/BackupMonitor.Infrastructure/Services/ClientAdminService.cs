using System.Text.Json;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>管理端客户端服务（设计书 15 客户端管理接口）</summary>
public interface IClientAdminService
{
    Task<PagedResult<ClientListItemDto>> GetListAsync(ClientQuery query, CancellationToken ct = default);
    Task<ClientDetailDto> GetDetailAsync(Guid clientId, CancellationToken ct = default);
    Task<IReadOnlyList<RecentAutoEnrollmentDto>> GetRecentAutomaticEnrollmentsAsync(
        int days = 7, int limit = 20, CancellationToken ct = default);
    Task<CertificateRenewalResponse> RenewCertificateAsync(Guid clientId, CancellationToken ct = default);
    Task<ClientMetricsHistoryDto> GetMetricsHistoryAsync(Guid clientId, int hours = 24, int limit = 1440, CancellationToken ct = default);
    Task<ApproveClientResponseDto> ApproveAsync(Guid clientId, CancellationToken ct = default);
    Task RejectAsync(Guid clientId, string? reason, CancellationToken ct = default);
    Task DisableAsync(Guid clientId, DisableClientRequest request, CancellationToken ct = default);
    Task RevokeAsync(Guid clientId, RevokeClientRequest request, CancellationToken ct = default);
    Task<DispatchCommandResponse> RefreshMetricsAsync(Guid clientId, CancellationToken ct = default);
}

/// <summary>客户端管理实现（列表/详情/审批签发证书/拒绝/禁用/注销/刷新主机状态）</summary>
public class ClientAdminService : IClientAdminService
{
    private static readonly AlertStatus[] ActiveAlertStatuses =
        [AlertStatus.Open, AlertStatus.Acknowledged, AlertStatus.InProgress];

    private readonly AppDbContext _db;
    private readonly CertificateAuthority _certificateAuthority;
    private readonly ICommandDispatcher _commands;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<ClientAdminService> _logger;

    public ClientAdminService(
        AppDbContext db,
        CertificateAuthority certificateAuthority,
        ICommandDispatcher commands,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<ClientAdminService> logger)
    {
        _db = db;
        _certificateAuthority = certificateAuthority;
        _commands = commands;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task<PagedResult<ClientListItemDto>> GetListAsync(ClientQuery query, CancellationToken ct = default)
    {
        var clients = _db.Clients.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!EnumMapping.TryParseSnakeCase<ClientStatus>(query.Status, out var status))
                throw new BusinessException("INVALID_REQUEST", $"无效的客户端状态：{query.Status}", 400);
            clients = clients.Where(c => c.Status == status);
        }

        if (query.GroupId is not null)
            clients = clients.Where(c => c.ClientGroupId == query.GroupId);

        if (!string.IsNullOrWhiteSpace(query.AgentVersion))
            clients = clients.Where(c => c.AgentVersion == query.AgentVersion);

        if (query.LastHeartbeatBefore is not null)
            clients = clients.Where(c => c.LastHeartbeatAt == null || c.LastHeartbeatAt < query.LastHeartbeatBefore);

        if (query.HasAlert is true)
        {
            clients = clients.Where(c => _db.Alerts.Any(a =>
                a.ClientId == c.Id && ActiveAlertStatuses.Contains(a.Status)));
        }
        else if (query.HasAlert is false)
        {
            clients = clients.Where(c => !_db.Alerts.Any(a =>
                a.ClientId == c.Id && ActiveAlertStatuses.Contains(a.Status)));
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            clients = clients.Where(c =>
                EF.Functions.ILike(c.Hostname, $"%{keyword}%")
                || EF.Functions.ILike(c.DisplayName, $"%{keyword}%"));
        }

        var totalCount = await clients.LongCountAsync(ct);

        var rows = await clients
            .OrderByDescending(c => c.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(c => new
            {
                c.Id,
                c.Hostname,
                c.DisplayName,
                c.ClientGroupId,
                GroupName = c.ClientGroup != null ? c.ClientGroup.Name : null,
                c.OsName,
                c.AgentVersion,
                c.Status,
                c.EnrollmentMode,
                c.CertificateExpiresAt,
                c.LastHeartbeatAt,
                c.CreatedAt
            })
            .ToListAsync(ct);

        var pageIds = rows.Select(r => r.Id).ToList();

        var alertCounts = await _db.Alerts
            .Where(a => a.ClientId != null && pageIds.Contains(a.ClientId.Value)
                        && ActiveAlertStatuses.Contains(a.Status))
            .GroupBy(a => a.ClientId!.Value)
            .Select(g => new { ClientId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var taskCounts = await _db.BackupTasks
            .Where(t => pageIds.Contains(t.ClientId))
            .GroupBy(t => t.ClientId)
            .Select(g => new { ClientId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var items = rows.Select(r => new ClientListItemDto
        {
            Id = r.Id,
            Hostname = r.Hostname,
            DisplayName = r.DisplayName,
            ClientGroupId = r.ClientGroupId,
            ClientGroupName = r.GroupName,
            OsName = r.OsName,
            AgentVersion = r.AgentVersion,
            Status = EnumMapping.ToSnakeCase(r.Status),
            EnrollmentMode = r.EnrollmentMode,
            CertificateExpiresAt = r.CertificateExpiresAt,
            CertificateRemainingDays = r.CertificateExpiresAt is null
                ? null
                : Math.Max(0, (int)Math.Ceiling((r.CertificateExpiresAt.Value - DateTime.UtcNow).TotalDays)),
            LastHeartbeatAt = r.LastHeartbeatAt,
            CreatedAt = r.CreatedAt,
            ActiveAlertCount = alertCounts.FirstOrDefault(a => a.ClientId == r.Id)?.Count ?? 0,
            TaskCount = taskCounts.FirstOrDefault(t => t.ClientId == r.Id)?.Count ?? 0
        }).ToList();

        return PagedResult<ClientListItemDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<ClientDetailDto> GetDetailAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking()
            .Include(c => c.ClientGroup)
            .Include(c => c.ApprovedByUser)
            .FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        var activeAlertCount = await _db.Alerts.CountAsync(a =>
            a.ClientId == clientId && ActiveAlertStatuses.Contains(a.Status), ct);
        var taskCount = await _db.BackupTasks.CountAsync(t => t.ClientId == clientId, ct);

        var disks = await _db.ClientDisks.AsNoTracking()
            .Where(d => d.ClientId == clientId)
            .OrderBy(d => d.DriveName)
            .ToListAsync(ct);

        var definitions = await _db.MonitoredServiceDefinitions.AsNoTracking()
            .Where(d => d.ClientId == clientId)
            .OrderBy(d => d.ServiceName)
            .ToListAsync(ct);

        var userSessions = await _db.ClientUserSessions.AsNoTracking()
            .Where(s => s.ClientId == clientId)
            .OrderBy(s => s.Username)
            .ThenBy(s => s.SessionId)
            .ToListAsync(ct);

        var lastHeartbeat = await _db.ClientHeartbeats.AsNoTracking()
            .Where(h => h.ClientId == clientId)
            .OrderByDescending(h => h.ReceivedAt)
            .Select(h => new ClientMetricsDto
            {
                CpuPercent = h.CpuPercent,
                AgentCpuPercent = h.AgentCpuPercent,
                MemoryPercent = h.MemoryPercent,
                MemoryTotalBytes = h.MemoryTotalBytes,
                MemoryAvailableBytes = h.MemoryAvailableBytes,
                AgentMemoryBytes = h.AgentMemoryBytes,
                NetworkSendBps = h.NetworkSendBps,
                NetworkReceiveBps = h.NetworkReceiveBps,
                AgentUptimeSeconds = h.AgentUptimeSeconds,
                SystemUptimeSeconds = h.SystemUptimeSeconds,
                ReceivedAt = h.ReceivedAt
            })
            .FirstOrDefaultAsync(ct);

        return new ClientDetailDto
        {
            Id = client.Id,
            Hostname = client.Hostname,
            DisplayName = client.DisplayName,
            ClientGroupId = client.ClientGroupId,
            ClientGroupName = client.ClientGroup?.Name,
            OsName = client.OsName,
            AgentVersion = client.AgentVersion,
            Status = EnumMapping.ToSnakeCase(client.Status),
            EnrollmentMode = client.EnrollmentMode,
            LastHeartbeatAt = client.LastHeartbeatAt,
            CreatedAt = client.CreatedAt,
            ActiveAlertCount = activeAlertCount,
            TaskCount = taskCount,

            MachineId = client.MachineId,
            OsVersion = client.OsVersion,
            Architecture = client.Architecture,
            IpAddresses = client.IpAddresses,
            ApprovedAt = client.ApprovedAt,
            ApprovedBy = client.ApprovedBy,
            ApprovedByName = client.ApprovedByUser?.DisplayName,
            LastConfigVersion = client.LastConfigVersion,
            CertificateThumbprint = client.CertificateThumbprint,
            CertificateExpiresAt = client.CertificateExpiresAt,
            CertificateRemainingDays = client.CertificateExpiresAt is null
                ? null
                : Math.Max(0, (int)Math.Ceiling((client.CertificateExpiresAt.Value - DateTime.UtcNow).TotalDays)),
            TimeOffsetSeconds = client.TimeOffsetSeconds,
            Notes = client.Notes,
            UpdatedAt = client.UpdatedAt,
            RowVersion = client.RowVersion,

            Disks = disks.Select(d => new ClientDiskDto
            {
                DriveName = d.DriveName,
                Filesystem = d.Filesystem,
                TotalBytes = d.TotalBytes,
                FreeBytes = d.FreeBytes,
                IsSourceVolume = d.IsSourceVolume,
                SampledAt = d.SampledAt
            }).ToList(),

            MonitoredServices = definitions.Select(d => new ClientServiceDto
            {
                DefinitionId = d.Id,
                ServiceName = d.ServiceName,
                DisplayName = d.DisplayName,
                ExpectedState = EnumMapping.ToSnakeCase(d.ExpectedState),
                ActualState = d.CurrentActualState is not null
                    ? EnumMapping.ToSnakeCase(d.CurrentActualState.Value) : null,
                LastSampledAt = d.CurrentSampledAt,
                AlertOnMismatch = d.AlertOnMismatch,
                Enabled = d.Enabled
            }).ToList(),

            UserSessions = userSessions.Select(s => new ClientUserSessionDto
            {
                SessionId = s.SessionId,
                Username = s.Username,
                Domain = s.Domain,
                State = s.State,
                ClientName = s.ClientName,
                ClientAddress = s.ClientAddress,
                IsRemote = s.IsRemote,
                LogonAt = s.LogonAt,
                SampledAt = s.SampledAt
            }).ToList(),

            LastMetrics = lastHeartbeat
        };
    }

    public async Task<IReadOnlyList<RecentAutoEnrollmentDto>> GetRecentAutomaticEnrollmentsAsync(
        int days = 7, int limit = 20, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        limit = Math.Clamp(limit, 1, 50);
        var from = DateTime.UtcNow.AddDays(-days);
        return await _db.Clients.AsNoTracking()
            .Where(c => c.EnrollmentMode == "lan_simple" && c.CreatedAt >= from)
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit)
            .Select(c => new RecentAutoEnrollmentDto
            {
                Id = c.Id,
                Hostname = c.Hostname,
                DisplayName = c.DisplayName,
                CreatedAt = c.CreatedAt,
                LastHeartbeatAt = c.LastHeartbeatAt
            })
            .ToListAsync(ct);
    }

    public async Task<ClientMetricsHistoryDto> GetMetricsHistoryAsync(
        Guid clientId,
        int hours = 24,
        int limit = 1440,
        CancellationToken ct = default)
    {
        if (!await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId, ct))
            throw new NotFoundException("客户端", clientId);

        hours = Math.Clamp(hours, 1, 168);
        limit = Math.Clamp(limit, 1, 2000);
        var to = DateTime.UtcNow;
        var from = to.AddHours(-hours);

        var points = await _db.ClientHeartbeats.AsNoTracking()
            .Where(h => h.ClientId == clientId && h.ReceivedAt >= from && h.ReceivedAt <= to)
            .OrderByDescending(h => h.ReceivedAt)
            .Take(limit)
            .Select(h => new ClientMetricsPointDto
            {
                ReceivedAt = h.ReceivedAt,
                CpuPercent = h.CpuPercent,
                AgentCpuPercent = h.AgentCpuPercent,
                MemoryPercent = h.MemoryPercent,
                MemoryTotalBytes = h.MemoryTotalBytes,
                MemoryAvailableBytes = h.MemoryAvailableBytes,
                AgentMemoryBytes = h.AgentMemoryBytes,
                NetworkSendBps = h.NetworkSendBps,
                NetworkReceiveBps = h.NetworkReceiveBps,
                AgentUptimeSeconds = h.AgentUptimeSeconds,
                SystemUptimeSeconds = h.SystemUptimeSeconds,
                ActiveCommandCount = h.ActiveCommandCount,
                ActiveUploadCount = h.ActiveUploadCount
            })
            .ToListAsync(ct);

        points.Reverse();
        return new ClientMetricsHistoryDto
        {
            ClientId = clientId,
            From = from,
            To = to,
            Points = points
        };
    }

    /// <summary>审批通过并签发客户端证书（设计书 15.3 / 10.2）</summary>
    public async Task<ApproveClientResponseDto> ApproveAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status != ClientStatus.PendingApproval)
            throw new BusinessException("CONFLICT",
                $"客户端当前状态为 {EnumMapping.ToSnakeCase(client.Status)}，只有待审批状态可以审批", 409);

        if (string.IsNullOrWhiteSpace(client.PublicKey))
            throw new BusinessException("INVALID_REQUEST", "注册公钥缺失，无法签发客户端证书", 422);

        var issued = await _certificateAuthority.IssueClientCertificateAsync(
            client.Id, client.Hostname, client.PublicKey);

        // 作废该客户端既有活动证书（重复审批场景兜底）
        var activeCertificates = await _db.ClientCertificates
            .Where(c => c.ClientId == clientId && c.Status == CertificateStatus.Active)
            .ToListAsync(ct);
        foreach (var certificate in activeCertificates)
        {
            certificate.Status = CertificateStatus.Revoked;
            certificate.RevokedAt = DateTime.UtcNow;
            certificate.RevokeReason = "被新签发的证书替代";
        }

        _db.ClientCertificates.Add(new ClientCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Thumbprint = issued.Thumbprint,
            SerialNumber = issued.SerialNumber,
            IssuedAt = issued.IssuedAt,
            ExpiresAt = issued.ExpiresAt,
            Status = CertificateStatus.Active,
            CertificatePem = issued.CertificatePem
        });

        var now = DateTime.UtcNow;
        client.Status = ClientStatus.Online;
        client.ApprovedAt = now;
        client.ApprovedBy = _context.UserId;
        client.CertificateThumbprint = issued.Thumbprint;
        client.CertificateExpiresAt = issued.ExpiresAt;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("client.approve", AuditResult.Success, "client", client.Id,
            afterData: $"{{\"status\":\"online\",\"certificateThumbprint\":\"{issued.Thumbprint}\"}}", ct: ct);

        _logger.LogInformation("客户端 {ClientId}({Hostname}) 审批通过并签发证书 {Thumbprint}",
            client.Id, client.Hostname, issued.Thumbprint);

        return new ApproveClientResponseDto
        {
            ClientId = client.Id,
            CertificateThumbprint = issued.Thumbprint,
            CertificateIssuedAt = issued.IssuedAt,
            CertificateExpiresAt = issued.ExpiresAt
        };
    }

    public async Task<CertificateRenewalResponse> RenewCertificateAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        var client = await _db.Clients
            .Include(c => c.Certificates)
            .FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CLIENT_DISABLED", "客户端已禁用或已注销，不能续签证书", 403);
        if (string.IsNullOrWhiteSpace(client.PublicKey))
            throw new BusinessException("INVALID_REQUEST", "客户端没有可用于续签的公钥", 409);

        var now = DateTime.UtcNow;
        var active = client.Certificates
            .Where(c => c.Status == CertificateStatus.Active)
            .OrderByDescending(c => c.ExpiresAt)
            .FirstOrDefault();
        if (active is not null && active.ExpiresAt > now.AddDays(30))
            throw new BusinessException("CERTIFICATE_RENEWAL_NOT_DUE", "客户端证书尚未进入 30 天续签窗口", 409);

        var issued = await _certificateAuthority.IssueClientCertificateAsync(
            client.Id,
            client.Hostname,
            client.PublicKey);

        foreach (var certificate in client.Certificates.Where(c => c.Status == CertificateStatus.Active))
        {
            certificate.Status = CertificateStatus.Superseded;
            certificate.RevokedAt = now;
            certificate.RevokeReason = "已由新证书替代，保留七天重叠期";
        }

        _db.ClientCertificates.Add(new ClientCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Thumbprint = issued.Thumbprint,
            SerialNumber = issued.SerialNumber,
            IssuedAt = issued.IssuedAt,
            ExpiresAt = issued.ExpiresAt,
            Status = CertificateStatus.Active,
            CertificatePem = issued.CertificatePem
        });
        client.CertificateThumbprint = issued.Thumbprint;
        client.CertificateExpiresAt = issued.ExpiresAt;
        client.Status = ClientStatus.Online;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(
            "client.certificate.renew",
            AuditResult.Success,
            "client",
            client.Id,
            afterData: JsonSerializer.Serialize(new
            {
                certificateThumbprint = issued.Thumbprint,
                certificateExpiresAt = issued.ExpiresAt,
                overlapDays = 7
            }),
            ct: ct);

        return new CertificateRenewalResponse
        {
            ClientId = client.Id,
            CertificatePem = issued.CertificatePem,
            CertificateThumbprint = issued.Thumbprint,
            CertificateIssuedAt = issued.IssuedAt,
            CertificateExpiresAt = issued.ExpiresAt
        };
    }

    /// <summary>拒绝注册（设计书 15.3，置为禁用并记录原因，注册轮询将得到 rejected）</summary>
    public async Task RejectAsync(Guid clientId, string? reason, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status != ClientStatus.PendingApproval)
            throw new BusinessException("CONFLICT",
                $"客户端当前状态为 {EnumMapping.ToSnakeCase(client.Status)}，只有待审批状态可以拒绝", 409);

        client.Status = ClientStatus.Disabled;
        client.Notes = string.IsNullOrWhiteSpace(reason) ? "注册审批被拒绝" : $"注册审批被拒绝：{reason}";

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("client.reject", AuditResult.Success, "client", client.Id,
            afterData: JsonSerializer.Serialize(new { reason }), ct: ct);
    }

    /// <summary>禁用客户端（设计书 15.4）</summary>
    public async Task DisableAsync(Guid clientId, DisableClientRequest request, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CONFLICT",
                $"客户端当前状态为 {EnumMapping.ToSnakeCase(client.Status)}，不允许禁用", 409);

        client.Status = ClientStatus.Disabled;
        if (!string.IsNullOrWhiteSpace(request.Reason))
            client.Notes = request.Reason;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("客户端");
        }

        await _audit.RecordAsync("client.disable", AuditResult.Success, "client", client.Id,
            afterData: JsonSerializer.Serialize(new { status = "disabled", request.Reason }), ct: ct);
    }

    /// <summary>注销客户端（设计书 15.5，吊销全部证书，不可恢复）</summary>
    public async Task RevokeAsync(Guid clientId, RevokeClientRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ValidationFailedException("注销原因必填");

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status == ClientStatus.Revoked)
            throw new BusinessException("CONFLICT", "客户端已注销", 409);

        var now = DateTime.UtcNow;
        client.Status = ClientStatus.Revoked;
        client.Notes = $"已注销：{request.Reason}";

        // 吊销该客户端全部活动证书，阻止继续通过 mTLS 认证
        var activeCertificates = await _db.ClientCertificates
            .Where(c => c.ClientId == clientId && c.Status == CertificateStatus.Active)
            .ToListAsync(ct);
        foreach (var certificate in activeCertificates)
        {
            certificate.Status = CertificateStatus.Revoked;
            certificate.RevokedAt = now;
            certificate.RevokeReason = request.Reason;
        }

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("client.revoke", AuditResult.Success, "client", client.Id,
            afterData: JsonSerializer.Serialize(new
            {
                status = "revoked",
                reason = request.Reason,
                revokedCertificates = activeCertificates.Count
            }), ct: ct);
    }

    /// <summary>下发刷新主机状态指令（设计书 15.6）</summary>
    public async Task<DispatchCommandResponse> RefreshMetricsAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CONFLICT", "客户端已禁用或已注销，不能下发指令", 409);

        var command = await _commands.CreateCommandAsync(
            clientId, CommandType.RefreshMetrics, priority: 50, createdBy: _context.UserId, ct: ct);

        return new DispatchCommandResponse { CommandId = command.Id };
    }
}
