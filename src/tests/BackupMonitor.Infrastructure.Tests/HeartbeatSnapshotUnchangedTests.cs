using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 心跳快照免报的服务端行为（审计 B-09）。
///
/// Agent 侧把磁盘 / 服务状态 / 用户会话算成一个稳定摘要，没变就整块不发。
/// 服务端大部分地方已经是「为 null 就不动库」的语义，唯独磁盘告警不是——
/// 它原先在 disks 为 null 时整段跳过，于是「快照没变」会变成「不再评估」：
/// 盘还是那么满，告警却既不会再触发也不会再恢复。这是这一条最容易漏的地方。
/// </summary>
[Collection("postgres")]
public class HeartbeatSnapshotUnchangedTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public HeartbeatSnapshotUnchangedTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        // AgentConfigService 依赖 CommandSigner（下发配置要签名），而 CommandSigner
        // 构造时就要一把真实的 RSA 私钥——空配置会让整条心跳链路在 DI 解析阶段就失败。
        sc.AddSingleton(SigningConfiguration());
        sc.AddSingleton<CommandSigner>();
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<IAgentConfigService, AgentConfigService>();
        sc.AddScoped<IAgentHeartbeatService, AgentHeartbeatService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>CommandSigner 需要一把真实的 RSA 私钥才能构造；测试里现造一把即可。</summary>
    private static IConfiguration SigningConfiguration()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:CommandSigningPrivateKey"] = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())
            })
            .Build();
    }

    [Fact]
    public async Task 快照未变时磁盘告警仍然按库里的记录评估()
    {
        var clientId = await SeedClientAsync();
        var alertKey = $"client:{clientId}:resource:disk:D:";

        // 第一次心跳带磁盘快照：源盘只剩 2%，应当告警
        await HeartbeatAsync(clientId, DiskSnapshot(freeBytes: 2, totalBytes: 100));
        Assert.Equal(AlertStatus.Open, await AlertStatusAsync(alertKey));

        // 第二次心跳快照未变，Disks 为 null。盘还是那么满，告警不能因此消失，
        // 更不能因为「不再评估」而在盘恢复之后仍然挂着。
        await HeartbeatAsync(clientId, disks: null);
        Assert.Equal(AlertStatus.Open, await AlertStatusAsync(alertKey));

        // 盘腾出来了：这一次带快照，告警恢复
        await HeartbeatAsync(clientId, DiskSnapshot(freeBytes: 80, totalBytes: 100));
        Assert.Equal(AlertStatus.Recovered, await AlertStatusAsync(alertKey));

        // 再来一次免报心跳：库里的记录已经是 80%，不该把告警重新点起来
        await HeartbeatAsync(clientId, disks: null);
        Assert.Equal(AlertStatus.Recovered, await AlertStatusAsync(alertKey));
    }

    [Fact]
    public async Task 快照未变时不覆盖库里的磁盘记录()
    {
        var clientId = await SeedClientAsync();
        await HeartbeatAsync(clientId, DiskSnapshot(freeBytes: 40, totalBytes: 100));
        await HeartbeatAsync(clientId, disks: null);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var disk = await db.ClientDisks.AsNoTracking().SingleAsync(d => d.ClientId == clientId);
        Assert.Equal(40, disk.FreeBytes);
        Assert.True(disk.IsSourceVolume);
    }

    // ---------- 基础设施 ----------

    private static List<HeartbeatDiskDto> DiskSnapshot(long freeBytes, long totalBytes) =>
    [
        new HeartbeatDiskDto
        {
            DriveName = "D:",
            TotalBytes = totalBytes,
            FreeBytes = freeBytes,
            IsSourceVolume = true
        }
    ];

    private async Task HeartbeatAsync(Guid clientId, List<HeartbeatDiskDto>? disks)
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAgentHeartbeatService>();
        await svc.ProcessAsync(clientId, new HeartbeatRequest
        {
            ClientTime = DateTime.UtcNow,
            ConfigVersion = 0,
            Disks = disks,
            SnapshotUnchanged = disks is null
        }, "test");
    }

    private async Task<AlertStatus> AlertStatusAsync(string alertKey)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Alerts.AsNoTracking().SingleAsync(a => a.AlertKey == alertKey)).Status;
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
            MachineId = $"iths-{suffix}",
            Hostname = $"iths-host-{suffix[..8]}",
            DisplayName = $"心跳测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }
}
