using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 概览页的客户端资源列表。
///
/// 这个查询要在分区表上按 client_id 取每台机器最近一次心跳，
/// 用的是 GroupBy(...).Select(g => g.OrderByDescending(...).First())——
/// 这种写法能不能被 EF Core 翻译成 SQL 取决于 provider，翻不动就是运行时异常，
/// 表现为概览页 500。必须打真库验证，不能靠"看着应该行"。
/// </summary>
[Collection("postgres")]
public class ClientResourceOverviewTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public ClientResourceOverviewTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<ICommandDispatcher, UnusedCommandDispatcher>();
        sc.AddScoped<SystemSettingsProvider>();
        // ClientAdminService 还兼管审批签发证书；本测试只碰只读的资源汇总，
        // 但 DI 仍要能把整个构造函数满足掉。
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<BackupMonitor.Infrastructure.Security.CertificateAuthority>();
        sc.AddScoped<IClientAdminService, ClientAdminService>();
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>
    /// 核心断言：查询能跑通，并且取到的是「最近一次」心跳而不是任意一次。
    /// </summary>
    [Fact]
    public async Task 取每台机器最近一次心跳()
    {
        var clientId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Clients.Add(new Client
            {
                Id = clientId,
                MachineId = Guid.NewGuid().ToString("N"),
                Hostname = "RES-" + Guid.NewGuid().ToString("N")[..8],
                DisplayName = "资源测试客户端",
                Status = ClientStatus.Online,
                LastHeartbeatAt = now
            });

            // 旧的在后、新的在前地插入，确保结果不是靠插入顺序碰对的。
            db.ClientHeartbeats.Add(NewHeartbeat(clientId, now.AddMinutes(-30), cpu: 11, memory: 22));
            db.ClientHeartbeats.Add(NewHeartbeat(clientId, now.AddMinutes(-1), cpu: 77, memory: 88));
            db.ClientHeartbeats.Add(NewHeartbeat(clientId, now.AddMinutes(-15), cpu: 33, memory: 44));

            db.ClientDisks.Add(new ClientDisk
            {
                Id = Guid.NewGuid(), ClientId = clientId, DriveName = "C:\\",
                TotalBytes = 1000, FreeBytes = 500, IsSourceVolume = false, SampledAt = now
            });
            db.ClientDisks.Add(new ClientDisk
            {
                Id = Guid.NewGuid(), ClientId = clientId, DriveName = "D:\\",
                TotalBytes = 1000, FreeBytes = 80, IsSourceVolume = true, SampledAt = now
            });
            db.ClientDisks.Add(new ClientDisk
            {
                Id = Guid.NewGuid(), ClientId = clientId, DriveName = "E:\\",
                TotalBytes = 1000, FreeBytes = 300, IsSourceVolume = true, SampledAt = now
            });

            await db.SaveChangesAsync();
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<IClientAdminService>();
            var rows = await clients.GetResourceOverviewAsync(limit: 200);

            var row = Assert.Single(rows, r => r.Id == clientId);
            Assert.Equal(77m, row.CpuPercent);
            Assert.Equal(88m, row.MemoryPercent);

            // 源盘里最紧张的是 D:（8%），而不是可用更少的非源盘或可用更多的 E:。
            Assert.Equal("D:\\", row.MinSourceDiskName);
            Assert.Equal(8m, row.MinSourceDiskFreePercent);
        }
    }

    /// <summary>从没上报过心跳的客户端也要出现在列表里，指标留空——否则「装了但没起来」的机器会整个消失。</summary>
    [Fact]
    public async Task 没有心跳的客户端也要列出()
    {
        var clientId = Guid.NewGuid();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Clients.Add(new Client
            {
                Id = clientId,
                MachineId = Guid.NewGuid().ToString("N"),
                Hostname = "NOHB-" + Guid.NewGuid().ToString("N")[..8],
                DisplayName = "无心跳客户端",
                Status = ClientStatus.Online
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var rows = await scope.ServiceProvider
                .GetRequiredService<IClientAdminService>()
                .GetResourceOverviewAsync(limit: 200);

            var row = Assert.Single(rows, r => r.Id == clientId);
            Assert.Null(row.CpuPercent);
            Assert.Null(row.MemoryPercent);
            Assert.Null(row.MinSourceDiskFreePercent);
            Assert.Null(row.LastHeartbeatAt);
        }
    }

    /// <summary>已注销的客户端没有观察价值，不该占据概览的位置。</summary>
    [Fact]
    public async Task 已注销客户端不出现在概览里()
    {
        var clientId = Guid.NewGuid();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Clients.Add(new Client
            {
                Id = clientId,
                MachineId = Guid.NewGuid().ToString("N"),
                Hostname = "REVOKED-" + Guid.NewGuid().ToString("N")[..8],
                DisplayName = "已注销客户端",
                Status = ClientStatus.Revoked
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var rows = await scope.ServiceProvider
                .GetRequiredService<IClientAdminService>()
                .GetResourceOverviewAsync(limit: 200);

            Assert.DoesNotContain(rows, r => r.Id == clientId);
        }
    }

    private static ClientHeartbeat NewHeartbeat(Guid clientId, DateTime receivedAt, decimal cpu, decimal memory) =>
        new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ReceivedAt = receivedAt,
            CpuPercent = cpu,
            MemoryPercent = memory
        };
}
