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
    Task<IReadOnlyList<ClientGroupOptionDto>> GetGroupOptionsAsync(CancellationToken ct = default);
    Task<ClientDetailDto> UpdateAsync(Guid clientId, UpdateClientRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<RecentAutoEnrollmentDto>> GetRecentAutomaticEnrollmentsAsync(
        int days = 7, int limit = 20, CancellationToken ct = default);
    Task ConfirmEnrollmentAsync(Guid clientId, CancellationToken ct = default);
    Task<CertificateRenewalResponse> RenewCertificateAsync(Guid clientId, CancellationToken ct = default);
    Task<ClientMetricsHistoryDto> GetMetricsHistoryAsync(Guid clientId, int hours = 24, int limit = 1440, CancellationToken ct = default);
    Task<IReadOnlyList<ClientResourceRowDto>> GetResourceOverviewAsync(int limit = 50, CancellationToken ct = default);
    Task<ApproveClientResponseDto> ApproveAsync(Guid clientId, CancellationToken ct = default);
    Task RejectAsync(Guid clientId, string? reason, CancellationToken ct = default);
    Task DisableAsync(Guid clientId, DisableClientRequest request, CancellationToken ct = default);
    Task EnableAsync(Guid clientId, EnableClientRequest request, CancellationToken ct = default);
    Task RevokeAsync(Guid clientId, RevokeClientRequest request, CancellationToken ct = default);

    /// <summary>删除一台已注销机器的记录（名下还有备份任务时拒绝）</summary>
    Task DeleteAsync(Guid clientId, CancellationToken ct = default);
    Task<DispatchCommandResponse> RefreshMetricsAsync(Guid clientId, CancellationToken ct = default);
}

/// <summary>客户端管理实现（列表/详情/审批签发证书/拒绝/禁用/注销/刷新主机状态）</summary>
public class ClientAdminService : IClientAdminService
{

    // ── 列表排序白名单（审查 P1-3）。键名与前端表头 data-sort-column 一致（camelCase）。
    private static readonly Dictionary<string, Func<IQueryable<Client>, bool, IQueryable<Client>>> ClientSorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["hostname"] = SortWhitelist.By<Client, string>(c => c.Hostname),
            ["displayName"] = SortWhitelist.By<Client, string>(c => c.DisplayName),
            ["status"] = SortWhitelist.By<Client, ClientStatus>(c => c.Status),
            ["agentVersion"] = SortWhitelist.By<Client, string?>(c => c.AgentVersion),
            ["lastHeartbeatAt"] = SortWhitelist.By<Client, DateTime?>(c => c.LastHeartbeatAt),
            ["createdAt"] = SortWhitelist.By<Client, DateTime>(c => c.CreatedAt)
        };

    private static readonly Func<IQueryable<Client>, bool, IQueryable<Client>> ClientSortFallback =
        SortWhitelist.By<Client, DateTime>(c => c.CreatedAt);
    private static readonly AlertStatus[] ActiveAlertStatuses =
        [AlertStatus.Open, AlertStatus.Acknowledged, AlertStatus.InProgress];

    // 「正在干活」的两种形态：上传会话还没落地，或者指令还没跑完。
    // 上传这一组与 BatchOperationService / BackupTaskService 判定 CLIENT_BUSY 用的是同一组状态——
    // 界面上锁的依据必须和服务端回绝的依据一致，否则会出现「按钮亮着，点了报 409」。
    private static readonly UploadStatus[] ActiveUploadStatuses = UploadSessionStatuses.InFlight;

    private static readonly CommandStatus[] RunningCommandStatuses =
        [CommandStatus.Pending, CommandStatus.Claimed, CommandStatus.Running];

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
        else if (!query.IncludeRevoked)
        {
            // 已注销的默认不列。一台机器重装后重新登记会留下一条同名的注销记录，
            // 而它在任何列表里都只有干扰作用——新建任务的机器选择框里两条同名机器，
            // 选错的那一条建出来的任务永远不会执行。要看它们请显式查 status=revoked。
            clients = clients.Where(c => c.Status != ClientStatus.Revoked);
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
            .ApplySort(query.SortBy, query.SortDescending, ClientSorts, ClientSortFallback)
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
                c.LastSeenAt,
                c.LastRemoteIp,
                // 自报网卡列表在列表页只为一件事服务：对端地址不是 IPv4 时，从里面挑一个能用的。
                // 整列 jsonb 拉回来比再发一次查询便宜，也比让界面自己去猜靠谱。
                c.IpAddresses,
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

        var uploadCounts = await _db.UploadSessions
            .Where(u => pageIds.Contains(u.ClientId) && ActiveUploadStatuses.Contains(u.Status))
            .GroupBy(u => u.ClientId)
            .Select(g => new { ClientId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var commandCounts = await _db.Commands
            .Where(c => pageIds.Contains(c.ClientId)
                        && RunningCommandStatuses.Contains(c.Status)
                        && c.ExpiresAt > DateTime.UtcNow)
            .GroupBy(c => c.ClientId)
            .Select(g => new { ClientId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var activeUploads = uploadCounts.FirstOrDefault(u => u.ClientId == r.Id)?.Count ?? 0;
            var runningCommands = commandCounts.FirstOrDefault(c => c.ClientId == r.Id)?.Count ?? 0;
            return new ClientListItemDto
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
            LastSeenAt = r.LastSeenAt,
            LastRemoteIp = r.LastRemoteIp,
            Ipv4Address = ResolveIpv4(r.LastRemoteIp, r.IpAddresses),
            CreatedAt = r.CreatedAt,
                ActiveAlertCount = alertCounts.FirstOrDefault(a => a.ClientId == r.Id)?.Count ?? 0,
                TaskCount = taskCounts.FirstOrDefault(t => t.ClientId == r.Id)?.Count ?? 0,
                ActiveUploadCount = activeUploads,
                RunningCommandCount = runningCommands,
                RuntimeState = ResolveRuntimeState(activeUploads, runningCommands)
            };
        }).ToList();

        return PagedResult<ClientListItemDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    /// <summary>
    /// 把 clients.ip_addresses 的 jsonb 原文解析成字符串数组。
    ///
    /// 这一列历史上由 JsonSerializer.Serialize(List&lt;string&gt;) 写入，
    /// 但它是 jsonb，理论上可以被人手工改成任何形状；解析失败时返回空列表，
    /// 不能让一条脏数据把整个客户端详情接口打成 500。
    /// </summary>
    private static List<string> ParseIpAddresses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// 给出这台机器"照着它去连"的 IPv4：对端地址本身是 IPv4 就用它，否则从自报网卡里挑第一个。
    ///
    /// 优先对端地址而不是自报列表，是因为它是唯一被证实连得通的那个；
    /// 只有在它是 IPv6（Windows 名称解析很容易把 Agent 领到 fe80:: 上去）时才需要回落。
    /// 两者都给不出 IPv4 时返回 null——纯 IPv6 环境是合法的，不该编一个地址出来。
    /// </summary>
    private static string? ResolveIpv4(string? lastRemoteIp, string? ipAddressesJson)
    {
        if (TryNormalizeIpv4(lastRemoteIp, out var fromPeer))
            return fromPeer;

        foreach (var candidate in ParseIpAddresses(ipAddressesJson))
            if (TryNormalizeIpv4(candidate, out var selfReported))
                return selfReported;

        return null;
    }

    /// <summary>
    /// 判定一个地址串是不是"能拿去连"的 IPv4，顺带还原 IPv4 映射形式。
    ///
    /// 排掉回环和 169.254 APIPA：前者只对机器自己成立，后者是 DHCP 拿不到地址时的兜底，
    /// 两个都放进 IP 列，等于给运维一个照着连必然连不上的地址，比留空更糟。
    /// 老客户端上报的网卡列表里这两类地址都还在（过滤是新版 Agent 才做的），
    /// 所以这道闸必须留在服务端。
    /// </summary>
    private static bool TryNormalizeIpv4(string? value, out string? ipv4)
    {
        ipv4 = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // 自报列表里可能带 %12 这类 IPv6 区域号，IPAddress.TryParse 认得，这里不额外处理。
        if (!System.Net.IPAddress.TryParse(value.Trim(), out var address))
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254))
            return false;

        ipv4 = address.ToString();
        return true;
    }

    /// <summary>
    /// 把两个计数收敛成界面上那一个词。上传优先于普通指令：正在传数据这件事
    /// 对"现在能不能动这台机器"的影响最大。
    /// </summary>
    private static string ResolveRuntimeState(int activeUploadCount, int runningCommandCount)
        => activeUploadCount > 0 ? "uploading"
        : runningCommandCount > 0 ? "working"
        : "idle";

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

        var activeUploads = await _db.UploadSessions.AsNoTracking()
            .Where(u => u.ClientId == clientId && ActiveUploadStatuses.Contains(u.Status))
            .Select(u => u.Task.Name)
            .ToListAsync(ct);
        var runningCommandCount = await _db.Commands.CountAsync(
            c => c.ClientId == clientId
                 && RunningCommandStatuses.Contains(c.Status)
                 && c.ExpiresAt > DateTime.UtcNow, ct);

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
            LastSeenAt = client.LastSeenAt,
            LastRemoteIp = client.LastRemoteIp,
            Ipv4Address = ResolveIpv4(client.LastRemoteIp, client.IpAddresses),
            CreatedAt = client.CreatedAt,
            ActiveAlertCount = activeAlertCount,
            TaskCount = taskCount,
            ActiveUploadCount = activeUploads.Count,
            RunningCommandCount = runningCommandCount,
            RuntimeState = ResolveRuntimeState(activeUploads.Count, runningCommandCount),
            ActiveUploadTaskNames = activeUploads.Distinct().ToList(),

            MachineId = client.MachineId,
            OsVersion = client.OsVersion,
            Architecture = client.Architecture,
            IpAddresses = ParseIpAddresses(client.IpAddresses),
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

    /// <summary>
    /// 全部客户端分组，供"改分组"下拉框使用。
    ///
    /// 分组数量是几十的量级（按机房/科室分），不分页；按名称排序而不是创建时间，
    /// 因为下拉框里人是照着名字找的。
    /// </summary>
    public async Task<IReadOnlyList<ClientGroupOptionDto>> GetGroupOptionsAsync(CancellationToken ct = default)
        => await _db.ClientGroups.AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new ClientGroupOptionDto { Id = g.Id, Name = g.Name, Code = g.Code })
            .ToListAsync(ct);

    /// <summary>
    /// 修改客户端的显示名称与所属分组。
    ///
    /// 这两项是纯管理端属性：心跳只更新时间偏移、对端 IP、网卡列表和状态，
    /// 从不回写 DisplayName / ClientGroupId，所以服务端改过的名字不会被 Agent 悄悄覆盖——
    /// 这是这个接口能成立的前提。主机名不在可改之列：它是机器的自我陈述，
    /// 改它等于让服务端记录和机器实际情况对不上。
    /// </summary>
    public async Task<ClientDetailDto> UpdateAsync(
        Guid clientId, UpdateClientRequest request, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        var before = JsonSerializer.Serialize(new { client.DisplayName, client.ClientGroupId });
        var changed = false;

        if (request.DisplayNameSpecified)
        {
            var displayName = request.DisplayName?.Trim();
            if (string.IsNullOrEmpty(displayName))
                throw new ValidationFailedException("显示名称不能为空");
            // 对齐 clients.display_name varchar(255)：越界让数据库去报，错误信息对管理员没有意义。
            if (displayName.Length > 255)
                throw new ValidationFailedException("显示名称不能超过 255 个字符");

            changed |= client.DisplayName != displayName;
            client.DisplayName = displayName;
        }

        if (request.ClientGroupIdSpecified)
        {
            // 传了一个不存在的分组 ID，靠外键约束去挡会得到一个 500；这里先说清楚。
            if (request.ClientGroupId is { } groupId
                && !await _db.ClientGroups.AnyAsync(g => g.Id == groupId, ct))
                throw new ValidationFailedException($"分组不存在：{groupId}");

            changed |= client.ClientGroupId != request.ClientGroupId;
            client.ClientGroupId = request.ClientGroupId;
        }

        if (changed)
        {
            try
            {
                // RowVersion 是并发令牌，SaveChanges 会带上它。
                // 这里不要求调用方回传版本号：心跳每 30 秒就会推进一次 row_version，
                // 拿列表上读到的版本来提交，几乎每次改名都会撞出一个假冲突。
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConcurrencyConflictException("客户端");
            }

            // 改名会把"这台机器是谁"的历史线索切断——三个月后翻旧告警的人，
            // 只有靠这条审计才能把当时的名字和现在的名字对上。
            await _audit.RecordAsync("client.updated", AuditResult.Success, "client", client.Id,
                beforeData: before,
                afterData: JsonSerializer.Serialize(new { client.DisplayName, client.ClientGroupId }),
                ct: ct);

            _logger.LogInformation("客户端 {ClientId}({Hostname}) 的名称/分组已修改为 {DisplayName}/{GroupId}",
                client.Id, client.Hostname, client.DisplayName, client.ClientGroupId);
        }

        return await GetDetailAsync(clientId, ct);
    }

    public async Task<IReadOnlyList<RecentAutoEnrollmentDto>> GetRecentAutomaticEnrollmentsAsync(
        int days = 7, int limit = 20, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        limit = Math.Clamp(limit, 1, 50);
        var from = DateTime.UtcNow.AddDays(-days);
        return await _db.Clients.AsNoTracking()
            // 已确认过的不再列出：这一类事项本来只有「时间到了自己消失」一条出路，
            // 于是队列里长期挂着清不掉的条目，人很快就不看待办页了。
            .Where(c => c.EnrollmentMode == "lan_simple" && c.CreatedAt >= from
                && c.EnrollmentReviewedAt == null)
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

    /// <summary>
    /// 概览页用的客户端资源快照：每台机器取最近一次心跳的指标 + 最紧张的备份源盘。
    ///
    /// 只取仍在服役的客户端（已禁用/已注销的没有观察价值），按"最需要关注"排序——
    /// 有活动告警的排最前，其次是内存吃紧的。默认排序如果按主机名，
    /// 那么真正需要看的那台机器会淹没在字母表中间。
    /// </summary>
    public async Task<IReadOnlyList<ClientResourceRowDto>> GetResourceOverviewAsync(
        int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        var clients = await _db.Clients.AsNoTracking()
            .Where(c => c.Status != ClientStatus.Disabled
                        && c.Status != ClientStatus.Revoked
                        && c.Status != ClientStatus.PendingApproval)
            .Select(c => new
            {
                c.Id,
                c.Hostname,
                c.DisplayName,
                c.Status,
                c.LastHeartbeatAt
            })
            .ToListAsync(ct);

        if (clients.Count == 0)
            return [];

        var ids = clients.Select(c => c.Id).ToList();

        // 每台机器最近一次心跳。分区表上按 client_id 分组取首行，
        // 走的是 (client_id, received_at DESC) 索引。
        var metrics = await _db.ClientHeartbeats.AsNoTracking()
            .Where(h => ids.Contains(h.ClientId))
            .GroupBy(h => h.ClientId)
            .Select(g => g.OrderByDescending(h => h.ReceivedAt).First())
            .ToListAsync(ct);
        var metricsById = metrics.ToDictionary(h => h.ClientId);

        // 备份源盘里最紧张的那块。非源盘不参与——系统盘满不满不是这个系统要回答的问题。
        var disks = await _db.ClientDisks.AsNoTracking()
            .Where(d => ids.Contains(d.ClientId) && d.IsSourceVolume
                        && d.TotalBytes != null && d.TotalBytes > 0 && d.FreeBytes != null)
            .Select(d => new { d.ClientId, d.DriveName, d.TotalBytes, d.FreeBytes })
            .ToListAsync(ct);
        var tightestDisk = disks
            .GroupBy(d => d.ClientId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(d => (decimal)d.FreeBytes!.Value / d.TotalBytes!.Value).First());

        var alertCounts = await _db.Alerts.AsNoTracking()
            .Where(a => a.ClientId != null && ids.Contains(a.ClientId.Value)
                        && ActiveAlertStatuses.Contains(a.Status))
            .GroupBy(a => a.ClientId!.Value)
            .Select(g => new { ClientId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var alertsById = alertCounts.ToDictionary(a => a.ClientId, a => a.Count);

        return clients
            .Select(c =>
            {
                metricsById.TryGetValue(c.Id, out var heartbeat);
                tightestDisk.TryGetValue(c.Id, out var disk);

                return new ClientResourceRowDto
                {
                    Id = c.Id,
                    Hostname = c.Hostname,
                    DisplayName = c.DisplayName,
                    Status = EnumMapping.ToSnakeCase(c.Status),
                    LastHeartbeatAt = c.LastHeartbeatAt,
                    CpuPercent = heartbeat?.CpuPercent,
                    MemoryPercent = heartbeat?.MemoryPercent,
                    MemoryTotalBytes = heartbeat?.MemoryTotalBytes,
                    MemoryAvailableBytes = heartbeat?.MemoryAvailableBytes,
                    AgentMemoryBytes = heartbeat?.AgentMemoryBytes,
                    SystemUptimeSeconds = heartbeat?.SystemUptimeSeconds,
                    MinSourceDiskName = disk?.DriveName,
                    MinSourceDiskFreePercent = disk is null
                        ? null
                        : Math.Round((decimal)disk.FreeBytes!.Value / disk.TotalBytes!.Value * 100, 2),
                    ActiveAlertCount = alertsById.GetValueOrDefault(c.Id)
                };
            })
            .OrderByDescending(r => r.ActiveAlertCount)
            .ThenByDescending(r => r.MemoryPercent ?? -1)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
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

    /// <summary>
    /// 确认一次自动登记：「我看过了，这台机器是预期内的」。
    ///
    /// 只把它从待办队列里摘掉，不改客户端状态、不动证书——它本来就已经在正常工作了。
    /// 重复确认是幂等的：批量点击、两个人同时处理都不该报错。
    /// </summary>
    public async Task ConfirmEnrollmentAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.EnrollmentReviewedAt is not null)
            return;

        client.EnrollmentReviewedAt = DateTime.UtcNow;
        client.EnrollmentReviewedBy = _context.UserId;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("client.confirm_enrollment", AuditResult.Success, "client", client.Id,
            afterData: $"{{\"enrollmentReviewedAt\":\"{client.EnrollmentReviewedAt:O}\"}}", ct: ct);

        _logger.LogInformation("客户端 {ClientId}({Hostname}) 的自动登记已确认", client.Id, client.Hostname);
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

        var cancelledSessions = await CancelInFlightUploadsAsync(clientId, "客户端已被禁用", ct);

        await _audit.RecordAsync("client.disable", AuditResult.Success, "client", client.Id,
            afterData: JsonSerializer.Serialize(new { status = "disabled", request.Reason, cancelledSessions }), ct: ct);
    }

    /// <summary>
    /// 把这台客户端还在途的上传会话一律取消。
    ///
    /// 不取消的话它们会一直占着全局上传名额，直到会话超时线（默认 6 小时）才回收——
    /// 而这台机器已经被禁用/注销，那几路传输永远不会有人来推进。
    /// 「先把这台机器停掉」在排障时是最自然的动作，代价不该是接下来半天所有别的机器都传不动。
    /// 返回取消的条数，落进审计。
    /// </summary>
    private async Task<int> CancelInFlightUploadsAsync(Guid clientId, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sessions = await _db.UploadSessions
            .Where(s => s.ClientId == clientId && UploadSessionStatuses.InFlight.Contains(s.Status))
            .ToListAsync(ct);

        if (sessions.Count == 0)
            return 0;

        foreach (var session in sessions)
        {
            session.Status = UploadStatus.Cancelled;
            session.ErrorCode = "CLIENT_UNAVAILABLE";
            session.ErrorMessage = reason;
            session.CompletedAt = now;
            session.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "客户端 {ClientId} 的 {Count} 个在途上传会话已取消：{Reason}", clientId, sessions.Count, reason);
        return sessions.Count;
    }

    /// <summary>
    /// 重新启用被禁用的客户端。
    ///
    /// 禁用此前是一条单行道：界面上没有回头路，接口层面也没有。而"先停掉这台机器，
    /// 等业务迁完再开回来"是个再正常不过的运维动作，不该只能靠改数据库解决。
    ///
    /// 只置回 offline 而不是 online——它现在到底在不在线由下一次心跳说了算
    /// （AgentHeartbeatService 会把 offline 翻成 online 或 certificate_expired）。
    /// 注销不可逆：证书已经吊销，机器必须重新登记审批，所以这里不接受 revoked。
    /// </summary>
    public async Task EnableAsync(Guid clientId, EnableClientRequest request, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status != ClientStatus.Disabled)
            throw new BusinessException("CONFLICT",
                $"客户端当前状态为 {EnumMapping.ToSnakeCase(client.Status)}，只有已禁用的客户端可以启用", 409);

        // 被拒绝的注册申请同样落在 disabled 上，但它从来没有过证书——
        // 把它"启用"只会得到一台永远连不上的机器，必须让它重新登记走审批。
        var hasActiveCertificate = await _db.ClientCertificates.AnyAsync(
            c => c.ClientId == clientId && c.Status == CertificateStatus.Active, ct);
        if (!hasActiveCertificate)
            throw new BusinessException("CONFLICT",
                "该客户端没有有效证书（注册被拒或证书已吊销），需要在客户端机器上重新登记并审批", 409);

        client.Status = ClientStatus.Offline;
        client.Notes = string.IsNullOrWhiteSpace(request.Reason)
            ? "已重新启用"
            : $"已重新启用：{request.Reason}";

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("客户端");
        }

        await _audit.RecordAsync("client.enable", AuditResult.Success, "client", client.Id,
            afterData: JsonSerializer.Serialize(new { status = "offline", request.Reason }), ct: ct);

        _logger.LogInformation("客户端 {ClientId}({Hostname}) 已重新启用", client.Id, client.Hostname);
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

        var cancelledSessions = await CancelInFlightUploadsAsync(clientId, "客户端已被注销", ct);

        await _audit.RecordAsync("client.revoke", AuditResult.Success, "client", client.Id,
            afterData: JsonSerializer.Serialize(new
            {
                status = "revoked",
                reason = request.Reason,
                revokedCertificates = activeCertificates.Count,
                cancelledSessions
            }), ct: ct);
    }

    /// <summary>
    /// 从列表里彻底删掉一台已注销的机器。
    ///
    /// 只对已注销的开放：注销是终点（证书吊销、心跳不再受理），删除只是把这条
    /// 已经没有意义的记录清掉。在用的机器要先注销，这一步不该有捷径。
    ///
    /// 名下还有备份任务时一律拒绝，而不是连带删除。任务删除会牵动备份集与
    /// 仓库目录里的真实文件，那套判断（哪些备份还活着、目录怎么清）在
    /// BackupTaskService 里，不该在这里复制一份——两份判断一旦分家，
    /// 「删机器」就会变成一条绕开备份保护的近路。
    /// </summary>
    public async Task DeleteAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status != ClientStatus.Revoked)
            throw new BusinessException("CONFLICT",
                "只有已注销的客户端才能删除记录。请先注销这台机器，再删除它的记录。", 409);

        var taskCount = await _db.BackupTasks.CountAsync(t => t.ClientId == clientId, ct);
        if (taskCount > 0)
            throw new BusinessException("CONFLICT",
                $"这台机器名下还有 {taskCount} 个备份任务。请先到「备份任务」页把它们删除"
                + "（那里会一并处理它们的备份集和仓库目录），再回来删除机器记录。", 409);

        // 备份集只能挂在任务下，任务为 0 时它必然也为 0——这里再确认一次，
        // 是为了万一有历史数据留下孤儿行时，报出的是这句话而不是一条外键异常。
        var setCount = await _db.BackupSets.CountAsync(s => s.ClientId == clientId, ct);
        if (setCount > 0)
            throw new BusinessException("CONFLICT",
                $"这台机器名下还有 {setCount} 份备份集，删除机器记录会让它们失去归属。请先处理这些备份集。", 409);

        var hostname = client.Hostname;
        var displayName = client.DisplayName;

        // 顺序由外键决定：告警引用客户端，投递引用告警；指令、候选、会话、
        // 队列项、心跳都是 NO ACTION，留一条就删不掉客户端本身。
        // clients 上挂着的证书、磁盘、指标、服务状态、会话、通知是 ON DELETE CASCADE，
        // 由数据库自己收尾。整段包在事务里：半截状态（客户端还在、它的告警没了）
        // 没有任何一条路径能修回去。
        var removed = await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // 还没发出去的投递先取消：notification_deliveries.alert_id 是「告警删除后置空」，
            // 删完告警它们会变成孤儿 pending 记录，派发器只看投递自身的状态，
            // 于是机器都删掉了，它的告警邮件还在继续重试。
            var cancelledDeliveries = await _db.NotificationDeliveries
                .Where(d => d.Alert != null && d.Alert.ClientId == clientId
                    && d.Status == NotificationStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, NotificationStatus.Cancelled), ct);

            var alerts = await _db.Alerts.Where(a => a.ClientId == clientId).ExecuteDeleteAsync(ct);
            var executionItems = await _db.ExecutionRunItems.Where(i => i.ClientId == clientId).ExecuteDeleteAsync(ct);
            var commands = await _db.Commands.Where(c => c.ClientId == clientId).ExecuteDeleteAsync(ct);
            var sessions = await _db.UploadSessions.Where(s => s.ClientId == clientId).ExecuteDeleteAsync(ct);
            var candidates = await _db.CandidateBackupSets.Where(c => c.ClientId == clientId).ExecuteDeleteAsync(ct);
            var heartbeats = await _db.ClientHeartbeats.Where(h => h.ClientId == clientId).ExecuteDeleteAsync(ct);

            _db.Clients.Remove(client);
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return new { cancelledDeliveries, alerts, executionItems, commands, sessions, candidates, heartbeats };
        });

        await _audit.RecordAsync("client.delete", AuditResult.Success, "client", clientId,
            beforeData: JsonSerializer.Serialize(new
            {
                clientId,
                hostname,
                displayName,
                removed.alerts,
                removed.cancelledDeliveries,
                removed.commands,
                removed.sessions,
                removed.candidates,
                removed.executionItems,
                removed.heartbeats
            }), ct: ct);

        _logger.LogInformation("已删除客户端记录 {ClientId}({Hostname})", clientId, hostname);
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
