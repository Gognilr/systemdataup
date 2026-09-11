using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 升级执行器（updater）的版本漂移。
///
/// 这一组存在的理由是一个很容易被忽略的事实：**updater 和 Agent 是分开升级的**。
/// 它住在安装目录的同级目录（…\BackupMonitor\Updater），而升级只换安装目录——
/// 它换不到自己。于是可以出现「Agent 已经是最新的，updater 还停在两年前」，
/// 而此前服务端完全看不出这一点：客户端列表里那台机器的 Agent 版本是最新的，一切正常。
///
/// 代价在 2026-09-11 兑现过一次：修好升级死循环的三处改动里有一处在 updater 里，
/// 于是它对全网所有存量机器都是不生效的，而没有任何地方能看出来。
/// </summary>
[Collection("postgres")]
public class UpdaterVersionDriftTests : IAsyncLifetime
{
    private const string Bundled = "1.3.2";

    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public UpdaterVersionDriftTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddScoped<IAgentVersionService, AgentVersionService>();
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>
    /// 这一条是整组的核心：Agent 最新、updater 旧，必须判为落后。
    ///
    /// 漏判的后果不是「界面上少一行字」，而是 updater 侧的修复在那台机器上根本没生效，
    /// 却没有任何人知道——直到下一次升级又踩进同一个坑。
    /// </summary>
    [Fact]
    public async Task Agent已是最新但升级执行器还是旧的照样算落后()
    {
        var clientId = await SeedClientAsync(agentVersion: Bundled, updaterVersion: "1.3.0");

        var status = await GetStatusAsync();

        var item = status.OutdatedClients.SingleOrDefault(c => c.ClientId == clientId);
        Assert.NotNull(item);
        Assert.Equal(Bundled, item!.AgentVersion);
        // 版本号要带回去：界面靠它说清「落后的是 updater 不是 Agent」，
        // 而这两种落后的处置完全不同——后者下发多少次升级都没用。
        Assert.Equal("1.3.0", item.UpdaterVersion);
    }

    /// <summary>两个都最新就不该出现在落后名单里——否则这份名单会永远清不空。</summary>
    [Fact]
    public async Task 两个版本都是最新时不算落后()
    {
        var clientId = await SeedClientAsync(agentVersion: Bundled, updaterVersion: Bundled);

        var status = await GetStatusAsync();

        Assert.DoesNotContain(status.OutdatedClients, c => c.ClientId == clientId);
    }

    /// <summary>
    /// 读不到 updater 版本**不算**落后。
    ///
    /// 为空有两种成因：这台机器压根没装 updater（老包装上来的，本来就走人工升级），
    /// 或者它还没升到会上报版本号的 1.3.2。两种都不是能靠告警催出来的事——
    /// 报了只会变成一条谁都清不掉的告警，而清不掉的告警很快就会被所有人忽略，
    /// 连带着把真正该看的那几条一起淹掉。
    /// </summary>
    [Fact]
    public async Task 升级执行器版本读不到时不算落后()
    {
        var clientId = await SeedClientAsync(agentVersion: Bundled, updaterVersion: null);

        var status = await GetStatusAsync();

        Assert.DoesNotContain(status.OutdatedClients, c => c.ClientId == clientId);
    }

    private async Task<BackupMonitor.Shared.Models.Admin.AgentVersionStatusDto> GetStatusAsync()
    {
        using var scope = _services.CreateScope();
        await SetBundledVersionAsync(scope.ServiceProvider);
        return await scope.ServiceProvider.GetRequiredService<IAgentVersionService>()
            .GetStatusAsync(CancellationToken.None);
    }

    /// <summary>用 system_settings 显式指定随附版本，免得测试依赖发布目录里的版本号文件。</summary>
    private static async Task SetBundledVersionAsync(IServiceProvider provider)
    {
        var db = provider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
            VALUES ({0}, to_jsonb({1}::text), false, now())
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """,
            AgentVersionService.BundledVersionKey,
            Bundled);

        // provider 有 60 秒缓存，不丢掉的话这次写进去的随附版本在本次断言里读不到。
        provider.GetRequiredService<SystemSettingsProvider>().Invalidate();
    }

    private async Task<Guid> SeedClientAsync(string? agentVersion, string? updaterVersion)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"updrift-{suffix}",
            Hostname = $"updrift-{suffix[..8]}",
            DisplayName = $"升级执行器漂移测试-{suffix[..8]}",
            Status = ClientStatus.Online,
            AgentVersion = agentVersion,
            UpdaterVersion = updaterVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }
}
