using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>业务系统探测的增删改查与「立即试一次」。</summary>
public interface IMonitoredEndpointService
{
    Task<IReadOnlyList<MonitoredEndpointDto>> ListAsync(CancellationToken ct = default);

    Task<MonitoredEndpointDto> CreateAsync(MonitoredEndpointUpsertDto request, CancellationToken ct = default);

    Task<MonitoredEndpointDto> UpdateAsync(Guid id, MonitoredEndpointUpsertDto request, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>按传进来的配置立刻探一次，不落库——用于配置页上的试探。</summary>
    Task<EndpointProbeResultDto> TryProbeAsync(MonitoredEndpointUpsertDto request, CancellationToken ct = default);
}

public class MonitoredEndpointService : IMonitoredEndpointService
{
    private readonly AppDbContext _db;
    private readonly IEndpointProber _prober;
    private readonly IAlertingService _alerting;

    public MonitoredEndpointService(AppDbContext db, IEndpointProber prober, IAlertingService alerting)
    {
        _db = db;
        _prober = prober;
        _alerting = alerting;
    }

    public async Task<IReadOnlyList<MonitoredEndpointDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await _db.MonitoredEndpoints.AsNoTracking()
            .OrderBy(e => e.Name)
            .Select(e => new
            {
                Entity = e,
                ClientName = e.Client == null ? null : e.Client.DisplayName
            })
            .ToListAsync(ct);

        return rows.Select(r => ToDto(r.Entity, r.ClientName)).ToList();
    }

    public async Task<MonitoredEndpointDto> CreateAsync(
        MonitoredEndpointUpsertDto request, CancellationToken ct = default)
    {
        var endpoint = new MonitoredEndpoint { Id = Guid.NewGuid() };
        Apply(endpoint, Validate(request));

        _db.MonitoredEndpoints.Add(endpoint);
        await _db.SaveChangesAsync(ct);

        return ToDto(endpoint, await ClientNameAsync(endpoint.ClientId, ct));
    }

    public async Task<MonitoredEndpointDto> UpdateAsync(
        Guid id, MonitoredEndpointUpsertDto request, CancellationToken ct = default)
    {
        var endpoint = await _db.MonitoredEndpoints.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("探测", id);

        Apply(endpoint, Validate(request));

        // 改完配置，之前那次失败的判定已经不作数了：地址、期望值都可能变了。
        // 计数不清零的话，改对之后第一次探测就会立刻触发告警（计数早就超阈值了）。
        endpoint.ConsecutiveFailures = 0;
        endpoint.LastProbedAt = null;

        await _db.SaveChangesAsync(ct);

        // 同理，旧告警也要消掉——它描述的是改之前那个配置的状态。
        await _alerting.RecoverAsync(EndpointProbeWorker.AlertKeyOf(endpoint), ct);

        return ToDto(endpoint, await ClientNameAsync(endpoint.ClientId, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var endpoint = await _db.MonitoredEndpoints.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("探测", id);

        _db.MonitoredEndpoints.Remove(endpoint);
        await _db.SaveChangesAsync(ct);

        // 探测都删了，它的告警不该还挂在告警中心里——那会变成一条谁都清不掉的红点。
        await _alerting.RecoverAsync(EndpointProbeWorker.AlertKeyOf(endpoint), ct);
    }

    public async Task<EndpointProbeResultDto> TryProbeAsync(
        MonitoredEndpointUpsertDto request, CancellationToken ct = default)
    {
        var probe = new MonitoredEndpoint { Id = Guid.Empty };
        Apply(probe, Validate(request));

        var result = await _prober.ProbeAsync(probe, capturePreview: true, ct);

        return new EndpointProbeResultDto
        {
            Success = result.Success,
            LatencyMs = result.LatencyMs,
            StatusCode = result.StatusCode,
            Error = result.Error,
            BodyPreview = result.BodyPreview
        };
    }

    private static MonitoredEndpointUpsertDto Validate(MonitoredEndpointUpsertDto request)
    {
        var type = (request.ProbeType ?? "tcp").Trim().ToLowerInvariant();
        if (type is not ("tcp" or "http"))
            throw new ValidationFailedException("探测类型只能是 tcp 或 http");

        request.ProbeType = type;
        request.Target = (request.Target ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(request.Target))
            throw new ValidationFailedException("请填写探测地址");

        if (type == "tcp")
        {
            if (request.Port is not (> 0 and <= 65535))
                throw new ValidationFailedException("TCP 探测必须填写 1~65535 之间的端口");

            // 状态码和正文是 HTTP 才有的概念，TCP 上留着就成了「填了却不生效」的输入框，
            // 而那种东西比没有更糟——人以为配了，实际没有。
            //
            // 慢阈值不在此列：TCP 连接耗时同样有意义，而且对堆开得大的 JVM 特别有意义——
            // Full GC 停顿期间连 accept 都会卡住，那是 OOM 之前最早能看见的信号。
            request.ExpectedStatus = null;
            request.ExpectedContent = null;
        }
        else
        {
            if (!Uri.TryCreate(request.Target, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ValidationFailedException("HTTP 探测的地址要是完整 URL，例如 http://172.16.11.175:8088/seeyon/index.jsp");
            }

            request.Port = null;
        }

        return request;
    }

    private static void Apply(MonitoredEndpoint endpoint, MonitoredEndpointUpsertDto request)
    {
        endpoint.Name = request.Name.Trim();
        endpoint.ClientId = request.ClientId;
        endpoint.ProbeType = request.ProbeType == "http" ? EndpointProbeType.Http : EndpointProbeType.Tcp;
        endpoint.Target = request.Target;
        endpoint.Port = request.Port;
        endpoint.ExpectedStatus = Blank(request.ExpectedStatus);
        endpoint.ExpectedContent = Blank(request.ExpectedContent);
        endpoint.TimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, 120);
        endpoint.IntervalSeconds = Math.Clamp(request.IntervalSeconds, 10, 86400);
        endpoint.FailureThreshold = Math.Clamp(request.FailureThreshold, 1, 100);
        endpoint.SlowMilliseconds = request.SlowMilliseconds;
        endpoint.Enabled = request.Enabled;
        endpoint.AlertOnFailure = request.AlertOnFailure;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<string?> ClientNameAsync(Guid? clientId, CancellationToken ct) =>
        clientId is null
            ? null
            : await _db.Clients.AsNoTracking()
                .Where(c => c.Id == clientId.Value)
                .Select(c => c.DisplayName)
                .FirstOrDefaultAsync(ct);

    private static MonitoredEndpointDto ToDto(MonitoredEndpoint e, string? clientName) => new()
    {
        Id = e.Id,
        Name = e.Name,
        ClientId = e.ClientId,
        ClientName = clientName,
        ProbeType = e.ProbeType == EndpointProbeType.Http ? "http" : "tcp",
        Target = e.Target,
        Port = e.Port,
        ExpectedStatus = e.ExpectedStatus,
        ExpectedContent = e.ExpectedContent,
        TimeoutSeconds = e.TimeoutSeconds,
        IntervalSeconds = e.IntervalSeconds,
        FailureThreshold = e.FailureThreshold,
        SlowMilliseconds = e.SlowMilliseconds,
        Enabled = e.Enabled,
        AlertOnFailure = e.AlertOnFailure,
        LastProbedAt = e.LastProbedAt,
        LastSuccessAt = e.LastSuccessAt,
        LastStatus = e.LastStatus,
        LastLatencyMs = e.LastLatencyMs,
        LastError = e.LastError,
        ConsecutiveFailures = e.ConsecutiveFailures
    };
}
