using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 客户端升级的分批推进（整改清单 2026-09-10 · R20）。
///
/// 这一组钉的是这次整改最核心的一条：**「指令执行完了」不是成功**。
/// 改之前的实现下发一条指令、Agent 回一句「包解压好了」，界面就显示成功，
/// 而机器上运行的还是旧程序——界面、指令回报、事实，三者里只有事实没人看得到。
///
/// 三条用例对应三个要害：
///   1. 第一批只放 1 台（金丝雀），其余的还在等——一个坏包同时推给 29 台就是 29 次上门；
///   2. 只有「心跳回来 **且** 版本号变了」才算成功，光有心跳不算；
///   3. 一批失败就刹车，后面的批次一台都不再推。
/// </summary>
[Collection("postgres")]
public class AgentUpgradeRolloutTests : IAsyncLifetime
{
    private const string TargetVersion = "9.9.9";
    private const string PackageUrl = "https://backup.example.com/downloads/BackupMonitor.Agent.zip";
    private const string PackageSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public AgentUpgradeRolloutTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(SigningConfiguration());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<IScheduledLockService, ScheduledLockService>();
        sc.AddSingleton<CommandSigner>();
        sc.AddScoped<CommandService>();
        sc.AddScoped<ICommandDispatcher>(sp => sp.GetRequiredService<CommandService>());
        sc.AddScoped<IAgentCommandService>(sp => sp.GetRequiredService<CommandService>());
        sc.AddScoped<IAgentUpgradeService, AgentUpgradeService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task 第一批只放一台其余的还在等()
    {
        var clients = await SeedClientsAsync(3);
        var upgradeId = await DispatchAsync(clients, "1,5,0");

        var detail = await GetAsync(upgradeId);
        Assert.Equal(1, detail.Targets.Count(t => t.Status == "dispatched"));
        Assert.Equal(2, detail.Targets.Count(t => t.Status == "waiting"));
    }

    [Fact]
    public async Task 心跳回来但版本号没变不算成功()
    {
        var clients = await SeedClientsAsync(2);
        var upgradeId = await DispatchAsync(clients, "1,0");

        // 心跳回来了，但自报的还是旧版本——这正是这次整改要认出来的那种「假成功」：
        // 指令执行完了、机器也活着，唯独程序没换。
        await ReportHeartbeatAsync(clients[0], "1.0.0");
        await AdvanceAsync(upgradeId);

        var detail = await GetAsync(upgradeId);
        Assert.Equal("dispatched", detail.Targets.Single(t => t.ClientId == clients[0]).Status);
        // 第一批没过，第二批一台都不该被放出去。
        Assert.All(detail.Targets.Where(t => t.ClientId != clients[0]), t => Assert.Equal("waiting", t.Status));
    }

    [Fact]
    public async Task 版本号变了之后才放行下一批()
    {
        var clients = await SeedClientsAsync(3);
        var upgradeId = await DispatchAsync(clients, "1,5,0");

        var canary = (await GetAsync(upgradeId)).Targets.Single(t => t.Status == "dispatched").ClientId;
        await ReportHeartbeatAsync(canary, TargetVersion);
        await AdvanceAsync(upgradeId);

        var detail = await GetAsync(upgradeId);
        Assert.Equal("succeeded", detail.Targets.Single(t => t.ClientId == canary).Status);
        Assert.Equal(2, detail.Targets.Count(t => t.Status == "dispatched"));
        Assert.Equal("running", detail.Status);
    }

    [Fact]
    public async Task 一台回报回滚之后后续批次不再下发()
    {
        var clients = await SeedClientsAsync(3);
        var upgradeId = await DispatchAsync(clients, "1,5,0");

        var canary = (await GetAsync(upgradeId)).Targets.Single(t => t.Status == "dispatched").ClientId;
        await ReportResultAsync(canary, "rolled_back", "换文件失败，已回滚到旧版本");
        await AdvanceAsync(upgradeId);

        var detail = await GetAsync(upgradeId);
        Assert.Equal("failed", detail.Status);
        Assert.Equal("failed", detail.Targets.Single(t => t.ClientId == canary).Status);
        // 剩下两台连指令都不该收到：一个坏包推给一台是一次上门，推给全网就是全网上门。
        Assert.Equal(2, detail.Targets.Count(t => t.Status == "cancelled"));
        Assert.DoesNotContain(detail.Targets, t => t.Status == "dispatched");
    }

    /// <summary>
    /// 下发前就已经是目标版本的机器，光靠版本号判不出「换没换」——
    /// 它从头到尾没变过，那条判据自动成立。这种情况必须等客户端自己回报，
    /// 否则一次什么都没换的升级会被判成功，回到 R20 要消灭的那种假成功。
    /// 版本号没递增的构建本来就不该当成一次升级发出去。
    /// </summary>
    [Fact]
    public async Task 下发前就已经是目标版本时不靠版本号判成功()
    {
        var clients = await SeedClientsAsync(1, agentVersion: TargetVersion);
        var upgradeId = await DispatchAsync(clients, "1,0");

        await ReportHeartbeatAsync(clients[0], TargetVersion);
        await AdvanceAsync(upgradeId);

        var detail = await GetAsync(upgradeId);
        Assert.Equal("dispatched", detail.Targets.Single().Status);
        Assert.Equal("running", detail.Status);

        // 新进程自己报了才算数。
        await ReportResultAsync(clients[0], "succeeded", null, runningVersion: TargetVersion);
        await AdvanceAsync(upgradeId);
        Assert.Equal("succeeded", (await GetAsync(upgradeId)).Status);
    }

    [Fact]
    public async Task 回报升级成功直接判成功()
    {
        var clients = await SeedClientsAsync(1);
        var upgradeId = await DispatchAsync(clients, "1,0");

        await ReportResultAsync(clients[0], "succeeded", null, runningVersion: TargetVersion);
        await AdvanceAsync(upgradeId);

        var detail = await GetAsync(upgradeId);
        Assert.Equal("succeeded", detail.Status);
        Assert.Equal(TargetVersion, detail.Targets.Single().ReportedVersion);
    }

    // ---------- 基础设施 ----------

    /// <summary>
    /// 升级指令一失败，界面上那台机器立刻就该显示失败和原因——不用等 30 分钟超时。
    ///
    /// 现场实证：Agent 回报 <c>UPGRADE_PACKAGE_URL_FORBIDDEN</c>（升级包地址和它记的
    /// 服务端地址对不上，一个写 IP 一个写主机名）之后，指令记录里写着这个原因，
    /// 而「客户端升级」页面上那一行仍然是「已下发，等版本号」、结果列一横杠。
    /// 一直等到超时才判失败，而超时给出的说法（「版本号仍是 X」）和真正的原因毫无关系。
    /// </summary>
    [Fact]
    public async Task 升级指令失败时立刻判失败并带上原因()
    {
        var clients = await SeedClientsAsync(1);
        var upgradeId = await DispatchAsync(clients, "1,0");

        var commandId = (await GetAsync(upgradeId)).Targets[0].CommandId;
        Assert.NotNull(commandId);

        await CompleteCommandAsync(clients[0], commandId!.Value, succeeded: false,
            resultCode: "UPGRADE_PACKAGE_URL_FORBIDDEN",
            resultMessage: "升级包地址不在本 Agent 的服务端上：172.16.11.130:5080");

        var target = (await GetAsync(upgradeId)).Targets[0];
        Assert.Equal("failed", target.Status);
        Assert.Equal("UPGRADE_PACKAGE_URL_FORBIDDEN", target.ErrorCode);
        Assert.Contains("172.16.11.130", target.ErrorMessage);
    }

    /// <summary>
    /// 「下载校验都过了，但这台机器上没有升级执行器」——指令回的是成功，
    /// 而程序一个字节没换。R20 要堵的正是这种成功，这里同样判失败。
    /// </summary>
    [Fact]
    public async Task 下载了但没装上同样判失败()
    {
        var clients = await SeedClientsAsync(1);
        var upgradeId = await DispatchAsync(clients, "1,0");
        var commandId = (await GetAsync(upgradeId)).Targets[0].CommandId;

        await CompleteCommandAsync(clients[0], commandId!.Value, succeeded: true,
            resultCode: "UPGRADE_DOWNLOADED_NOT_INSTALLED",
            resultMessage: "升级包已下载并校验，但本机没有安装升级执行器");

        var target = (await GetAsync(upgradeId)).Targets[0];
        Assert.Equal("failed", target.Status);
        Assert.Equal("UPGRADE_DOWNLOADED_NOT_INSTALLED", target.ErrorCode);
    }

    /// <summary>
    /// 指令成功不等于升级成功：那要等这台机器心跳自报新版本号。
    /// 这一条钉住「别把指令回报又当成结论」——那正是 R20 之前的老毛病。
    /// </summary>
    [Fact]
    public async Task 指令成功不直接判成功()
    {
        var clients = await SeedClientsAsync(1);
        var upgradeId = await DispatchAsync(clients, "1,0");
        var commandId = (await GetAsync(upgradeId)).Targets[0].CommandId;

        await CompleteCommandAsync(clients[0], commandId!.Value, succeeded: true,
            resultCode: "UPGRADE_STAGED", resultMessage: "已暂存，等空闲窗口安装");

        Assert.Equal("dispatched", (await GetAsync(upgradeId)).Targets[0].Status);
    }

    /// <summary>走 Agent 实际那条路：先领取指令，再回报完成。</summary>
    private async Task CompleteCommandAsync(
        Guid clientId, Guid commandId, bool succeeded, string resultCode, string resultMessage)
    {
        await using var scope = _services.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IAgentCommandService>();

        await commands.ClaimAsync(clientId, new ClaimCommandsRequest
        {
            MaxItems = 10,
            SupportedCommandTypes = ["upgrade_agent"]
        });
        await commands.ReportCompletedAsync(clientId, commandId, new CommandCompletedRequest
        {
            Success = succeeded,
            ResultCode = resultCode,
            ResultMessage = resultMessage
        });
    }

    private async Task<List<Guid>> SeedClientsAsync(int count, string agentVersion = "1.0.0")
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var ids = new List<Guid>();

        for (var i = 0; i < count; i++)
        {
            var client = new Client
            {
                Id = Guid.NewGuid(),
                MachineId = $"upg-{Guid.NewGuid():N}",
                Hostname = $"upgrade-host-{i}",
                DisplayName = $"升级测试机 {i}",
                Status = ClientStatus.Online,
                AgentVersion = agentVersion,
                ApprovedAt = now.AddDays(-1),
                LastHeartbeatAt = now.AddMinutes(-1),
                CreatedAt = now.AddDays(-1),
                UpdatedAt = now
            };
            db.Clients.Add(client);
            ids.Add(client.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<Guid> DispatchAsync(List<Guid> clients, string batchPlan)
    {
        await using var scope = _services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAgentUpgradeService>();
        await service.DispatchAsync(new UpgradeAgentRequestDto
        {
            ClientIds = clients,
            TargetVersion = TargetVersion,
            PackageUrl = PackageUrl,
            PackageSha256 = PackageSha,
            BatchPlan = batchPlan
        }, idempotencyKey: null);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AgentUpgrades.AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => u.Id)
            .FirstAsync();
    }

    private async Task<AgentUpgradeDetailDto> GetAsync(Guid upgradeId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentUpgradeService>().GetAsync(upgradeId);
    }

    private async Task AdvanceAsync(Guid upgradeId)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAgentUpgradeService>().AdvanceAsync(upgradeId);
    }

    /// <summary>模拟一次心跳：更新版本号与心跳时刻，与 AgentHeartbeatService 写的那两列一致。</summary>
    private async Task ReportHeartbeatAsync(Guid clientId, string agentVersion)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = await db.Clients.SingleAsync(c => c.Id == clientId);
        client.AgentVersion = agentVersion;
        client.LastHeartbeatAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task ReportResultAsync(Guid clientId, string status, string? message, string? runningVersion = null)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAgentUpgradeService>().ReportResultAsync(clientId,
            new AgentUpgradeResultRequest
            {
                Status = status,
                TargetVersion = TargetVersion,
                RunningVersion = runningVersion,
                Message = message
            });
    }

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
}

/// <summary>
/// 分批计划与版本号比对（纯函数，不进库）。
///
/// 这几条看着琐碎，但它们各自守着一个「静默走偏」的口子：
/// 少算一台机器的后果是那台永远停在 waiting，而且不会有任何人发现；
/// 版本号比对判宽了，就又回到了「假成功」。
/// </summary>
public class AgentUpgradeBatchPlanTests
{
    [Fact]
    public void 末位零表示剩下的全放()
    {
        Assert.Equal([1, 5, 24], AgentUpgradeService.ResolveBatchSizes("1,5,0", 30));
    }

    [Fact]
    public void 计划台数不够时余下的补成一批()
    {
        // 「1,5」只安排了 6 台，剩下的 4 台必须有归属——否则它们永远不会被下发。
        Assert.Equal([1, 5, 4], AgentUpgradeService.ResolveBatchSizes("1,5", 10));
    }

    [Fact]
    public void 机器数少于计划时不产生空批次()
    {
        Assert.Equal([1, 1], AgentUpgradeService.ResolveBatchSizes("1,5,0", 2));
    }

    [Fact]
    public void 分批计划为空时取默认的金丝雀计划()
    {
        Assert.Equal(AgentUpgradeService.DefaultBatchPlan, AgentUpgradeService.NormalizeBatchPlan(null));
        Assert.Equal(AgentUpgradeService.DefaultBatchPlan, AgentUpgradeService.NormalizeBatchPlan("  "));
    }

    /// <summary>
    /// 自动填充出来的版本号必须自己过得了自己的校验。
    ///
    /// 实测的 agent-version.txt 是 `1.1.0+346e5f98…`（40 位提交号，整串 45 字符），
    /// 而 targetVersion 是 varchar(32) / MaxLength(32)——原样填进表单，
    /// 点「下发」收到的是「targetVersion 不能超过 32 字符」。
    /// 手工敲版本号的年代碰不到这一条，它只会在自动填充上线那天出现。
    /// </summary>
    [Fact]
    public void 自动填充的版本号去掉构建后缀且不超过字段长度()
    {
        var stripped = AgentUpgradeService.StripBuildSuffix("1.1.0+346e5f984f0288253362139c3586ce0e6a93975f");

        Assert.Equal("1.1.0", stripped);
        Assert.True(stripped.Length <= 32);
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.2.0+abc123", "1.2.0", true)]
    [InlineData("1.1.0", "1.2.0", false)]
    [InlineData(null, "1.2.0", false)]
    [InlineData("", "1.2.0", false)]
    public void 版本号比对宁可判不成功也不判错成功(string? reported, string target, bool expected)
    {
        Assert.Equal(expected, AgentUpgradeService.VersionMatches(reported, target));
    }
}
