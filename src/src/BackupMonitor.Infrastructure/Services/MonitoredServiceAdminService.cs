using System.Text.Json;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 关键 Windows 服务监控的配置入口。
///
/// monitored_service_definitions 这张表从第一版就在，Agent 会按它逐个探测服务状态、
/// 心跳上报、界面也有展示区块——缺的一直只是"谁来往里写"。界面上那句
/// 「在客户端配置中添加需要监控的 Windows 服务」指向的是一个不存在的地方。
/// </summary>
public interface IMonitoredServiceAdminService
{
    Task<IReadOnlyList<ClientServiceDto>> ListAsync(Guid clientId, CancellationToken ct = default);

    Task<ClientServiceDto> CreateAsync(Guid clientId, CreateMonitoredServiceRequest request, CancellationToken ct = default);

    Task<ClientServiceDto> UpdateAsync(Guid clientId, Guid definitionId, UpdateMonitoredServiceRequest request, CancellationToken ct = default);

    Task DeleteAsync(Guid clientId, Guid definitionId, CancellationToken ct = default);

    /// <summary>下发 list_services 指令，让客户端把自己装了哪些服务报上来。</summary>
    Task<DispatchCommandResponse> DispatchListServicesAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>轮询 list_services 结果；已被监控的服务会被标出来。</summary>
    Task<ListServicesResultDto> GetListServicesResultAsync(Guid commandId, CancellationToken ct = default);
}

public class MonitoredServiceAdminService : IMonitoredServiceAdminService
{
    /// <summary>服务列表是交互式的：人在界面上等，等不到会重来，留一条僵尸指令没有意义。</summary>
    private static readonly TimeSpan ListServicesTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _commands;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;

    public MonitoredServiceAdminService(
        AppDbContext db,
        ICommandDispatcher commands,
        ICurrentContext context,
        IAuditRecorder audit)
    {
        _db = db;
        _commands = commands;
        _context = context;
        _audit = audit;
    }

    public async Task<IReadOnlyList<ClientServiceDto>> ListAsync(Guid clientId, CancellationToken ct = default)
    {
        await EnsureClientExistsAsync(clientId, ct);

        var definitions = await _db.MonitoredServiceDefinitions.AsNoTracking()
            .Where(d => d.ClientId == clientId)
            .OrderBy(d => d.ServiceName)
            .ToListAsync(ct);

        return definitions.Select(Project).ToList();
    }

    public async Task<ClientServiceDto> CreateAsync(
        Guid clientId, CreateMonitoredServiceRequest request, CancellationToken ct = default)
    {
        var client = await EnsureClientExistsAsync(clientId, ct);

        var serviceName = (request.ServiceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new BusinessException("INVALID_REQUEST", "服务名不能为空", 400);

        var expected = EnumMapping.ParseSnakeCase<ServiceExpectedState>(request.ExpectedState, nameof(request.ExpectedState));

        // 同一台机器上同一个服务只该有一条定义，否则告警会重复、状态会互相覆盖。
        var duplicate = await _db.MonitoredServiceDefinitions
            .AnyAsync(d => d.ClientId == clientId && d.ServiceName.ToLower() == serviceName.ToLower(), ct);
        if (duplicate)
            throw new BusinessException("CONFLICT", $"该客户端已经在监控服务 {serviceName}", 409);

        var definition = new MonitoredServiceDefinition
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ServiceName = serviceName,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? serviceName : request.DisplayName.Trim(),
            ExpectedState = expected,
            AlertOnMismatch = request.AlertOnMismatch,
            Enabled = request.Enabled
        };

        _db.MonitoredServiceDefinitions.Add(definition);
        await BumpConfigRevisionAsync(client, ct);
        await _db.SaveChangesAsync(ct);
        await DispatchConfigSyncAsync(clientId, ct);

        await _audit.RecordAsync(
            "client.monitored_service.create", AuditResult.Success, "client", clientId,
            afterData: JsonSerializer.Serialize(new { definition.ServiceName, expectedState = request.ExpectedState }),
            ct: ct);

        return Project(definition);
    }

    public async Task<ClientServiceDto> UpdateAsync(
        Guid clientId, Guid definitionId, UpdateMonitoredServiceRequest request, CancellationToken ct = default)
    {
        var client = await EnsureClientExistsAsync(clientId, ct);
        var definition = await _db.MonitoredServiceDefinitions
            .FirstOrDefaultAsync(d => d.Id == definitionId && d.ClientId == clientId, ct)
            ?? throw new NotFoundException("监控服务定义", definitionId);

        var before = JsonSerializer.Serialize(new
        {
            definition.DisplayName,
            ExpectedState = EnumMapping.ToSnakeCase(definition.ExpectedState),
            definition.AlertOnMismatch,
            definition.Enabled
        });

        definition.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? definition.ServiceName
            : request.DisplayName.Trim();
        definition.ExpectedState = EnumMapping.ParseSnakeCase<ServiceExpectedState>(
            request.ExpectedState, nameof(request.ExpectedState));
        definition.AlertOnMismatch = request.AlertOnMismatch;
        definition.Enabled = request.Enabled;

        await BumpConfigRevisionAsync(client, ct);
        await _db.SaveChangesAsync(ct);
        await DispatchConfigSyncAsync(clientId, ct);

        await _audit.RecordAsync(
            "client.monitored_service.update", AuditResult.Success, "client", clientId,
            beforeData: before,
            afterData: JsonSerializer.Serialize(new
            {
                definition.ServiceName,
                definition.DisplayName,
                ExpectedState = EnumMapping.ToSnakeCase(definition.ExpectedState),
                definition.AlertOnMismatch,
                definition.Enabled
            }),
            ct: ct);

        return Project(definition);
    }

    public async Task DeleteAsync(Guid clientId, Guid definitionId, CancellationToken ct = default)
    {
        var client = await EnsureClientExistsAsync(clientId, ct);
        var definition = await _db.MonitoredServiceDefinitions
            .FirstOrDefaultAsync(d => d.Id == definitionId && d.ClientId == clientId, ct)
            ?? throw new NotFoundException("监控服务定义", definitionId);

        var serviceName = definition.ServiceName;
        _db.MonitoredServiceDefinitions.Remove(definition);
        await BumpConfigRevisionAsync(client, ct);
        await _db.SaveChangesAsync(ct);
        await DispatchConfigSyncAsync(clientId, ct);

        await _audit.RecordAsync(
            "client.monitored_service.delete", AuditResult.Success, "client", clientId,
            beforeData: JsonSerializer.Serialize(new { serviceName }),
            ct: ct);
    }

    public async Task<DispatchCommandResponse> DispatchListServicesAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await EnsureClientExistsAsync(clientId, ct);
        if (client.Status == ClientStatus.PendingApproval)
            throw new BusinessException("CONFLICT", "客户端尚未审批通过，不能读取其服务列表", 409);
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CONFLICT", "客户端已禁用或已注销，不能读取其服务列表", 409);

        var command = await _commands.CreateCommandAsync(
            clientId,
            CommandType.ListServices,
            payload: new ListServicesCommandPayload { IncludeStopped = true },
            priority: 10,
            ttl: ListServicesTtl,
            createdBy: _context.UserId,
            ct: ct);

        await _audit.RecordAsync(
            "client.list_services", AuditResult.Success, "client", clientId,
            afterData: JsonSerializer.Serialize(new { commandId = command.Id }), ct: ct);

        return new DispatchCommandResponse { CommandId = command.Id };
    }

    public async Task<ListServicesResultDto> GetListServicesResultAsync(Guid commandId, CancellationToken ct = default)
    {
        var command = await _db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commandId, ct)
            ?? throw new NotFoundException("指令", commandId);

        if (command.CommandType != CommandType.ListServices)
            throw new BusinessException("INVALID_REQUEST", "该指令不是服务列表指令", 400);

        var dto = new ListServicesResultDto
        {
            CommandId = command.Id,
            Status = EnumMapping.ToSnakeCase(command.Status),
            ResultCode = command.ResultCode,
            ResultMessage = command.ResultMessage
        };

        if (command.Status != CommandStatus.Succeeded || string.IsNullOrWhiteSpace(command.ResultPayload))
            return dto;

        InstalledServiceListDto? result;
        try
        {
            result = JsonSerializer.Deserialize<InstalledServiceListDto>(command.ResultPayload, ResultJson);
        }
        catch (JsonException)
        {
            return dto;
        }

        if (result is null)
            return dto;

        // 已经在监控的标出来，界面据此禁用重复添加——比让人点了才收到 409 友好。
        var monitored = await _db.MonitoredServiceDefinitions.AsNoTracking()
            .Where(d => d.ClientId == command.ClientId)
            .Select(d => d.ServiceName)
            .ToListAsync(ct);
        var monitoredSet = new HashSet<string>(monitored, StringComparer.OrdinalIgnoreCase);
        foreach (var service in result.Services)
            service.AlreadyMonitored = monitoredSet.Contains(service.ServiceName);

        dto.Result = result;
        return dto;
    }

    // ---------- 私有辅助 ----------

    /// <summary>
    /// 推进客户端配置修订号。少了这一步，改动会存进库但永远到不了客户端：
    /// Agent 只在心跳里的 requiredConfigVersion 变大时才重新拉配置。
    ///
    /// 必须越过「任务版本最大值」，而不是只比自己上一次大。生效版本取的是
    /// max(任务版本, 修订号)：客户端有个任务停在 config_version=100 时，
    /// 把修订号从 1 加到 2 完全不会改变生效版本，改动照样到不了客户端——
    /// 这个洞正是本方法存在的理由，栽在它自己身上就太可惜了。
    /// </summary>
    private async Task BumpConfigRevisionAsync(Core.Entities.Client.Client client, CancellationToken ct)
    {
        var taskVersion = await _db.BackupTasks
            .Where(t => t.ClientId == client.Id)
            .Select(t => (long?)t.ConfigVersion)
            .MaxAsync(ct) ?? 0;

        client.ConfigRevision = Math.Max(client.ConfigRevision, taskVersion) + 1;
    }

    /// <summary>
    /// 顺手下发一条 sync_config：不发也能生效（下一次心跳会发现版本变了），
    /// 但那要等最多一个心跳周期（默认 60 秒）。发了就是一个轮询周期（10 秒）。
    /// 客户端不在线时这条指令过期作废，不影响正确性。
    /// </summary>
    private async Task DispatchConfigSyncAsync(Guid clientId, CancellationToken ct)
    {
        try
        {
            await _commands.CreateCommandAsync(
                clientId, CommandType.SyncConfig,
                priority: 20,
                ttl: TimeSpan.FromMinutes(30),
                createdBy: _context.UserId,
                ct: ct);
        }
        catch (Exception)
        {
            // 配置已经落库，版本号也推进了，下一次心跳照样会同步。
            // 这条只是加速手段，失败不该让整个保存操作失败。
        }
    }

    private async Task<Core.Entities.Client.Client> EnsureClientExistsAsync(Guid clientId, CancellationToken ct) =>
        await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct)
        ?? throw new NotFoundException("客户端", clientId);

    private static ClientServiceDto Project(MonitoredServiceDefinition definition) => new()
    {
        DefinitionId = definition.Id,
        ServiceName = definition.ServiceName,
        DisplayName = definition.DisplayName,
        ExpectedState = EnumMapping.ToSnakeCase(definition.ExpectedState),
        ActualState = definition.CurrentActualState is null
            ? null
            : EnumMapping.ToSnakeCase(definition.CurrentActualState.Value),
        LastSampledAt = definition.CurrentSampledAt,
        AlertOnMismatch = definition.AlertOnMismatch,
        Enabled = definition.Enabled
    };
}
