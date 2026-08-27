using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 客户端 IP 的可见性与新鲜度。
///
/// 原先 clients.ip_addresses 只在 AgentRegistrationService 里写过一次，之后永不刷新：
/// DHCP 续租或换网段之后库里那份就永久过期，而界面上看不出它是旧的。
/// 现在有两条独立的线：
/// 一、Agent 自报的网卡列表并进心跳快照，变了才发，服务端只在收到时覆盖；
/// 二、last_remote_ip 是服务端在连接层实际看到的对端地址，每次心跳都刷新，
///     多网卡机器上到底哪个地址在用，只有它能回答。
/// </summary>
[Collection("postgres")]
public class ClientIpVisibilityTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public ClientIpVisibilityTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

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
        sc.AddScoped<IAgentConfigService, AgentConfigService>();
        sc.AddScoped<IAgentHeartbeatService, AgentHeartbeatService>();
        sc.AddScoped<IClientAdminService, ClientAdminService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task 心跳记录服务端观测到的对端IP()
    {
        var clientId = await SeedClientAsync();
        await HeartbeatAsync(clientId, ipAddresses: null);

        // NullCurrentContext.ClientIp 是 127.0.0.1，模拟 UseForwardedHeaders 之后
        // 拿到的真实对端地址——它不依赖 Agent 上报，所以永远是新鲜的。
        var client = await LoadAsync(clientId);
        Assert.Equal("127.0.0.1", client.LastRemoteIp);
    }

    [Fact]
    public async Task 网卡列表变化时覆盖旧值()
    {
        var clientId = await SeedClientAsync();

        await HeartbeatAsync(clientId, ["192.168.1.37"]);
        Assert.Equal("[\"192.168.1.37\"]", (await LoadAsync(clientId)).IpAddresses);

        // 换了网段：这正是原先永远刷不过来的场景
        await HeartbeatAsync(clientId, ["10.0.5.12", "fe80::1"]);
        var detail = await DetailAsync(clientId);
        Assert.Equal(["10.0.5.12", "fe80::1"], detail.IpAddresses);
    }

    [Fact]
    public async Task 快照未变时不清空已有的网卡列表()
    {
        var clientId = await SeedClientAsync();
        await HeartbeatAsync(clientId, ["192.168.1.37"]);

        // IpAddresses 为 null 的语义是「没变」，不是「没有」。
        // 当成「没有」去覆盖，等于每分钟把这一列擦掉一次——
        // 而快照免报意味着绝大多数心跳都是这一种。
        await HeartbeatAsync(clientId, ipAddresses: null);

        Assert.Equal(["192.168.1.37"], (await DetailAsync(clientId)).IpAddresses);
    }

    [Fact]
    public async Task 详情接口给出解析后的数组而不是jsonb原文()
    {
        var clientId = await SeedClientAsync();
        await HeartbeatAsync(clientId, ["192.168.1.37", "fe80::a1b2"]);

        var detail = await DetailAsync(clientId);

        // 原先直接把 jsonb 原文透出去，界面上显示的就是带方括号和引号的
        // ["192.168.1.37","fe80::a1b2"]。jsonb 是存储细节，不该穿过 API。
        Assert.Equal(2, detail.IpAddresses.Count);
        Assert.DoesNotContain(detail.IpAddresses, ip => ip.Contains('[') || ip.Contains('"'));
    }

    [Fact]
    public async Task 脏数据不会把详情接口打成500()
    {
        var clientId = await SeedClientAsync();

        // ip_addresses 是 jsonb，理论上可以被人手工改成任何形状。
        // 解析失败必须退化成空列表，而不是让整个客户端详情不可用。
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE clients SET ip_addresses = '{\"unexpected\": true}'::jsonb WHERE id = {0}", clientId);
        }

        Assert.Empty((await DetailAsync(clientId)).IpAddresses);
    }

    [Fact]
    public async Task 列表项带上对端IP()
    {
        var clientId = await SeedClientAsync();
        await HeartbeatAsync(clientId, ["192.168.1.37"]);

        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientAdminService>();
        var page = await svc.GetListAsync(new ClientQuery { Page = 1, PageSize = 200 });

        // 放进列表项而不是只放详情：找一台机器最常用的线索就是 IP，
        // 为看一眼 IP 逐台点进详情抽屉是没有道理的。
        var row = Assert.Single(page.Items.Where(i => i.Id == clientId));
        Assert.Equal("127.0.0.1", row.LastRemoteIp);
    }

    // ---------- 基础设施 ----------

    private async Task HeartbeatAsync(Guid clientId, List<string>? ipAddresses)
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAgentHeartbeatService>();
        await svc.ProcessAsync(clientId, new HeartbeatRequest
        {
            ClientTime = DateTime.UtcNow,
            ConfigVersion = 0,
            IpAddresses = ipAddresses,
            SnapshotUnchanged = ipAddresses is null
        }, "test");
    }

    private async Task<Client> LoadAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
    }

    private async Task<ClientDetailDto> DetailAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientAdminService>();
        return await svc.GetDetailAsync(clientId);
    }

    private async Task<Guid> SeedClientAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"itip-{suffix}",
            Hostname = $"itip-host-{suffix[..8]}",
            DisplayName = $"IP 可见性测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }
}
