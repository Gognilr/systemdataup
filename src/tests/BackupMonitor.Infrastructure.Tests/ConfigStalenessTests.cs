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
/// 客户端配置卡住的可见性（整改清单 2026-09-11 · R27）。
///
/// 来自一次现场故障：一台客户端从 08:01 到 14:42 每 10 秒失败一次配置同步
/// （CONFIG_SIGNATURE_INVALID——它存的服务端签名公钥与服务端当前的对不上），
/// 6.5 小时一个配置都没下去。而这次失败是**纯客户端侧**的：它自己把服务端的响应拒了，
/// 服务端从头到尾不知道。客户端列表里它是绿的「在线」，心跳一次没断。
///
/// 后果不是「配置晚到一会儿」：任务的增删改在这台机器上不生效、新加的任务永远下不去，
/// 而界面上一切正常。**一个不生效的配置比一个报错的配置危险得多**，因为没有人会去找它。
/// </summary>
[Collection("postgres")]
public class ConfigStalenessTests : IAsyncLifetime
{
    private const string AlertCategory = "agent_config_stale";

    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public ConfigStalenessTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
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

    /// <summary>刚落后一下不报警：改完配置到下一次同步之间，每一台都会短暂落后。</summary>
    [Fact]
    public async Task 刚落后还不报警但会记下起点()
    {
        var clientId = await SeedClientAsync(requiredVersion: 5);

        await HeartbeatAsync(clientId, reportedConfigVersion: 3);

        Assert.NotNull(await StaleSinceAsync(clientId));
        Assert.Null(await FindAlertAsync(clientId));
    }

    /// <summary>卡过阈值就报——而且要说清楚「在线但配置没下去」这件事本身。</summary>
    [Fact]
    public async Task 卡过阈值之后报警()
    {
        var clientId = await SeedClientAsync(requiredVersion: 5);
        await HeartbeatAsync(clientId, reportedConfigVersion: 3);

        // 把起点推到 3 小时前：这台机器已经卡了一个上午
        await SetStaleSinceAsync(clientId, DateTime.UtcNow.AddHours(-3));
        await HeartbeatAsync(clientId, reportedConfigVersion: 3);

        var alert = await FindAlertAsync(clientId);
        Assert.NotNull(alert);
        Assert.Equal(AlertLevel.Critical, alert!.Level);
        // 正文要带上两个版本号和卡了多久——否则收到告警的人无从判断严重程度
        Assert.Contains("3", alert.Message);
        Assert.Contains("5", alert.Message);
        Assert.Contains("CONFIG_SIGNATURE_INVALID", alert.Message);
    }

    /// <summary>配置追上了就清起点并恢复告警——否则红点会永久挂在那里。</summary>
    [Fact]
    public async Task 配置追上之后恢复()
    {
        var clientId = await SeedClientAsync(requiredVersion: 5);
        await HeartbeatAsync(clientId, reportedConfigVersion: 3);
        await SetStaleSinceAsync(clientId, DateTime.UtcNow.AddHours(-3));
        await HeartbeatAsync(clientId, reportedConfigVersion: 3);
        Assert.NotNull(await FindAlertAsync(clientId));

        await HeartbeatAsync(clientId, reportedConfigVersion: 5);

        Assert.Null(await StaleSinceAsync(clientId));
        Assert.Equal(AlertStatus.Recovered, (await FindAlertAsync(clientId))!.Status);
    }

    /// <summary>版本本来就一致的机器，一个字都不该说。</summary>
    [Fact]
    public async Task 版本一致时不记起点也不报警()
    {
        var clientId = await SeedClientAsync(requiredVersion: 5);

        await HeartbeatAsync(clientId, reportedConfigVersion: 5);

        Assert.Null(await StaleSinceAsync(clientId));
        Assert.Null(await FindAlertAsync(clientId));
    }

    // ---------- 基础设施 ----------

    private async Task HeartbeatAsync(Guid clientId, long reportedConfigVersion)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAgentHeartbeatService>().ProcessAsync(
            clientId,
            new HeartbeatRequest
            {
                ClientTime = DateTime.UtcNow,
                ConfigVersion = reportedConfigVersion,
                SnapshotUnchanged = true
            },
            "test");
    }

    private async Task<DateTime?> StaleSinceAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId).Select(c => c.ConfigStaleSince).FirstAsync();
    }

    private async Task SetStaleSinceAsync(Guid clientId, DateTime since)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Clients.Where(c => c.Id == clientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConfigStaleSince, since));
    }

    private async Task<Core.Entities.Alert.Alert?> FindAlertAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Alerts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && a.Category == AlertCategory);
    }

    /// <summary>
    /// 造一台客户端，并把「要求的配置版本」顶到 <paramref name="requiredVersion"/>。
    /// 要求版本由任务版本与客户端配置修订号共同决定，这里用后者抬即可。
    /// </summary>
    private async Task<Guid> SeedClientAsync(long requiredVersion)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"cfgstale-{suffix}",
            Hostname = $"cfgstale-{suffix[..8]}",
            DisplayName = $"配置卡住测试-{suffix[..8]}",
            Status = ClientStatus.Online,
            ConfigRevision = requiredVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static IConfiguration SigningConfiguration()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pkcs8 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:CommandSigningPrivateKey"] = pkcs8
            })
            .Build();
    }
}
