using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 客户端存活判定。
///
/// 原先唯一的证据是 last_heartbeat_at，而全系统只有心跳那一个端点会写它。
/// 于是判定回答的其实是「它有没有按时汇报」，而不是「它还在不在」——
/// 一台正在传 5 GB 备份、每几秒就要领指令 / 报扫描进度 / 传分块的机器，
/// 只要心跳被大文件哈希拖住，180 秒后就变「疑似离线」、300 秒后「离线」并发严重告警。
/// 而它正在好好干活。这种告警响几次之后就没人看了，真离线的时候也一样没人看。
///
/// 现在任何一次已认证的 Agent 请求都会写 last_seen_at（ClientLastSeenTracker），
/// 判定取两者的较晚者。这两条用例钉的就是这件事的两个方向：
/// 还在说话的不判离线；真的不说话了照样判离线。
/// </summary>
[Collection("postgres")]
public class ClientLivenessTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public ClientLivenessTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>心跳早就过阈值了，但它一直在跟服务端说话——这台机器在线。</summary>
    [Fact]
    public async Task 心跳落后但一直在通信的客户端不判离线()
    {
        var now = DateTime.UtcNow;
        var clientId = await SeedClientAsync(
            lastHeartbeatAt: now.AddMinutes(-30),   // 远超 300 秒的离线线
            lastSeenAt: now.AddSeconds(-5));        // 五秒前刚领过一条指令

        await RunLivenessAsync();

        Assert.Equal(ClientStatus.Online, await StatusOfAsync(clientId));
    }

    /// <summary>真的不说话了就照样判离线——放宽判据不等于放弃判据。</summary>
    [Fact]
    public async Task 两条线索都静默的客户端仍然判离线()
    {
        var now = DateTime.UtcNow;
        var clientId = await SeedClientAsync(
            lastHeartbeatAt: now.AddMinutes(-30),
            lastSeenAt: now.AddMinutes(-25));

        await RunLivenessAsync();

        Assert.Equal(ClientStatus.Offline, await StatusOfAsync(clientId));
    }

    /// <summary>从来没发过心跳、但刚刚有过请求的（刚审批完那一小段）同样算在线。</summary>
    [Fact]
    public async Task 没有心跳只有通信记录时也算在线()
    {
        var clientId = await SeedClientAsync(
            lastHeartbeatAt: null,
            lastSeenAt: DateTime.UtcNow.AddSeconds(-10));

        await RunLivenessAsync();

        Assert.Equal(ClientStatus.Online, await StatusOfAsync(clientId));
    }

    // ---------- 基础设施 ----------

    private async Task RunLivenessAsync()
    {
        var worker = new SystemWatchdogWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SystemWatchdogWorker>.Instance);

        using var scope = _services.CreateScope();
        await worker.RunClientLivenessAsync(scope, CancellationToken.None);
    }

    private async Task<Guid> SeedClientAsync(DateTime? lastHeartbeatAt, DateTime? lastSeenAt)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"live-{Guid.NewGuid():N}",
            Hostname = "liveness-host",
            DisplayName = "存活判定用",
            Status = ClientStatus.Online,
            ApprovedAt = now.AddDays(-1),
            LastHeartbeatAt = lastHeartbeatAt,
            LastSeenAt = lastSeenAt,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private async Task<ClientStatus> StatusOfAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => c.Status)
            .SingleAsync();
    }
}
