using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 业务系统探测工作器（V047）。
///
/// 被监控的 Windows 服务回答「进程在不在」，这个回答「业务用不用得了」——
/// 而这类系统最常见的故障恰恰落在两者之间：进程好好的，业务已经用不了。
/// U8 的加密狗掉了、Tomcat 里的 webapp OOM 了、数据库连接池耗尽了，
/// 服务状态对这些一律显示正常。
///
/// 单实例（进程内单 BackgroundService + scheduled_locks 数据库锁），幂等。
/// </summary>
public class EndpointProbeWorker : BackgroundService
{
    /// <summary>工作器醒来的节奏（秒）。每条探测有自己的间隔，这个只决定轮询粒度。</summary>
    public const string TickKey = "endpoint_probe_tick_seconds";

    public const string LockKey = "worker:endpoint_probe";

    /// <summary>锁 TTL 要大于单轮最坏耗时：N 条探测串行、每条最长 120 秒超时。</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EndpointProbeWorker> _logger;

    public EndpointProbeWorker(IServiceScopeFactory scopeFactory, ILogger<EndpointProbeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("业务系统探测工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var tickSeconds = 20;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>();
                tickSeconds = Math.Clamp(await settings.GetIntAsync(TickKey, 20, stoppingToken), 5, 600);

                var scheduledLock = scope.ServiceProvider.GetRequiredService<IScheduledLockService>();
                if (await scheduledLock.TryAcquireAsync(LockKey, LockTtl, stoppingToken))
                {
                    try
                    {
                        await RunPassAsync(scope, stoppingToken);
                    }
                    finally
                    {
                        await scheduledLock.ReleaseAsync(LockKey, CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "业务系统探测轮询异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(tickSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prober = scope.ServiceProvider.GetRequiredService<IEndpointProber>();
        var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();

        var now = DateTime.UtcNow;
        var candidates = await db.MonitoredEndpoints
            .Where(e => e.Enabled)
            .ToListAsync(ct);

        var due = candidates
            .Where(e => e.LastProbedAt is null
                        || e.LastProbedAt.Value.AddSeconds(Math.Clamp(e.IntervalSeconds, 10, 86400)) <= now)
            .ToList();

        if (due.Count == 0)
            return;

        foreach (var endpoint in due)
        {
            ct.ThrowIfCancellationRequested();
            await ProbeOneAsync(endpoint, prober, ct);
        }

        await db.SaveChangesAsync(ct);

        // 告警按**客户端**合并，不是一条探测一条告警。
        //
        // 一台 U8 宕了，三个检查项会同时失败——分开报就是群里三条一模一样的消息，
        // 而同一件事说三遍，人就开始整体划过去。合成一条还更好读：
        // 「3 项中 2 项不通」和「1 项不通」是完全不同的故障，前者多半整套服务停了，
        // 后者可能只是加密狗掉了。正常的那项也列出来——它说明机器和网络是通的。
        //
        // 告警键用 client:{id}:endpoints，和其它客户端告警同一个前缀，
        // 这样维护模式那条 client:{id}:% 的静默能一并罩住它。
        var clientIds = due.Where(e => e.ClientId is not null)
            .Select(e => e.ClientId!.Value).Distinct().ToList();

        foreach (var clientId in clientIds)
            await EvaluateClientGroupAsync(db, alerting, clientId, ct);

        // 没关联客户端的探测（以后探交换机、NAS 这类）没有可归拢的归属，仍然一条一告警。
        foreach (var endpoint in due.Where(e => e.ClientId is null))
            await EvaluateStandaloneAsync(alerting, endpoint, ct);
    }

    private static async Task ProbeOneAsync(
        MonitoredEndpoint endpoint, IEndpointProber prober, CancellationToken ct)
    {
        var result = await prober.ProbeAsync(endpoint, capturePreview: false, ct);

        endpoint.LastProbedAt = DateTime.UtcNow;
        endpoint.LastLatencyMs = result.LatencyMs;
        endpoint.LastError = result.Success ? null : result.Error;
        endpoint.LastStatus = result.Success ? "up" : "down";

        if (result.Success)
        {
            endpoint.LastSuccessAt = endpoint.LastProbedAt;
            endpoint.ConsecutiveFailures = 0;
            return;
        }

        // 连续失败到阈值才报。一次抖动就报警，练几次人就麻木了——
        // 而麻木之后，连真出事的那条也会被一起划走。
        endpoint.ConsecutiveFailures++;
    }

    /// <summary>一台客户端名下所有探测合起来判一次，报一条或消一条。</summary>
    private async Task EvaluateClientGroupAsync(
        AppDbContext db, IAlertingService alerting, Guid clientId, CancellationToken ct)
    {
        var all = await db.MonitoredEndpoints.AsNoTracking()
            .Where(e => e.ClientId == clientId && e.Enabled)
            .OrderBy(e => e.Name)
            .ToListAsync(ct);

        var clientName = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => c.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "客户端";

        var key = GroupAlertKeyOf(clientId);
        var failing = all.Where(IsAlerting).ToList();

        if (failing.Count == 0)
        {
            await alerting.RecoverAsync(key, ct);
            return;
        }

        await alerting.RaiseAsync(
            key,
            AlertLevel.Critical,
            AlertCategoryCatalog.EndpointDown,
            $"{clientName} 业务探测失败：{all.Count} 项中 {failing.Count} 项不通",
            BuildGroupMessage(all),
            clientId: clientId,
            ct: ct);
    }

    private async Task EvaluateStandaloneAsync(
        IAlertingService alerting, MonitoredEndpoint endpoint, CancellationToken ct)
    {
        var key = AlertKeyOf(endpoint);
        if (!IsAlerting(endpoint))
        {
            await alerting.RecoverAsync(key, ct);
            return;
        }

        await alerting.RaiseAsync(
            key,
            AlertLevel.Critical,
            AlertCategoryCatalog.EndpointDown,
            $"{endpoint.Name} 探测失败",
            $"{DescribeTarget(endpoint)}：{endpoint.LastError}（已连续失败 {endpoint.ConsecutiveFailures} 次）",
            ct: ct);
    }

    /// <summary>这一条是不是已经该报了：开着告警、且连续失败到了它自己的阈值。</summary>
    internal static bool IsAlerting(MonitoredEndpoint e) =>
        e.AlertOnFailure && e.ConsecutiveFailures >= Math.Clamp(e.FailureThreshold, 1, 100);

    /// <summary>
    /// 合并告警的正文：好的坏的都列出来。
    ///
    /// 正常的那几项不是废话——它们说明机器和网络是通的，坏的是上面跑的那套业务，
    /// 而这个区分决定了人接下来是去重启服务还是去看网络。
    /// </summary>
    internal static string BuildGroupMessage(IReadOnlyList<MonitoredEndpoint> all)
    {
        var lines = all.Select(e => IsAlerting(e)
            ? $"✗ {e.Name}  {DescribeTarget(e)} — {e.LastError ?? "失败"}"
            : $"✓ {e.Name}  {DescribeTarget(e)} — 正常"
              + (e.LastLatencyMs is not null ? $"（{e.LastLatencyMs} ms）" : string.Empty));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 合并后的告警键。
    ///
    /// 刻意和其它客户端告警同一个 client:{id}: 前缀——维护模式下发的静默
    /// 用的就是 client:{id}:% 这个模式，前缀对不上就罩不住它。
    /// </summary>
    internal static string GroupAlertKeyOf(Guid clientId) => $"client:{clientId}:endpoints";

    /// <summary>没关联客户端时的告警键，一条探测一个。</summary>
    internal static string AlertKeyOf(MonitoredEndpoint endpoint) => $"endpoint:{endpoint.Id}:down";

    internal static string DescribeTarget(MonitoredEndpoint endpoint) =>
        endpoint.ProbeType == EndpointProbeType.Http
            ? endpoint.Target
            : $"{endpoint.Target}:{endpoint.Port}";
}
