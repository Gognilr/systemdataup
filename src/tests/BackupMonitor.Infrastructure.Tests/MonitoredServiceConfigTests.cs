using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 关键 Windows 服务监控的配置链路。
///
/// 这套功能的表、探测、上报、展示从第一版就齐了，唯独没有"谁来往里写"。
/// 补写入口时最容易漏的不是 CRUD 本身，而是配置版本——
/// 改动存进了库、界面也显示了，但 Agent 永远不会重新拉配置，
/// 于是新加的监控服务一辈子到不了客户端，且没有任何报错。
/// 这里的用例首先钉住那条链路。
/// </summary>
[Collection("postgres")]
public class MonitoredServiceConfigTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public MonitoredServiceConfigTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton<ICurrentContext, NullCurrentContext>();
        services.AddScoped<IAuditRecorder, DbAuditRecorder>();
        services.AddSingleton(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // 指令签名用的 RSA 私钥；测试里现造一把即可。
            ["Security:CommandSigningPrivateKey"] = Convert.ToBase64String(
                System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKey())
        }).Build() as IConfiguration);
        services.AddSingleton<CommandSigner>();
        services.AddScoped<IAlertingService, NoopAlertingService>();
        services.AddScoped<ICommandDispatcher, CommandService>();
        services.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SystemSettingsProvider>.Instance));
        services.AddScoped<IAgentConfigService, AgentConfigService>();
        services.AddScoped<IMonitoredServiceAdminService, MonitoredServiceAdminService>();
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> CreateClientAsync(IServiceScope scope, string hostname)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = new Core.Entities.Client.Client
        {
            Id = Guid.NewGuid(),
            Hostname = hostname,
            DisplayName = hostname,
            MachineId = Guid.NewGuid().ToString("N"),
            Status = ClientStatus.Online,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    /// <summary>
    /// 加一条监控，Agent 必须能感知到配置变了。
    ///
    /// requiredConfigVersion 原先只由 backup_tasks.config_version 算出，
    /// 而监控服务的增删改动不了任何任务的版本号——这条用例就是钉住那个洞。
    /// 注意客户端一个任务都没有：那正是原实现最彻底失效的场景（版本恒为 0）。
    /// </summary>
    [Fact]
    public async Task 新增监控服务会推进配置版本使客户端重新拉取()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IMonitoredServiceAdminService>();
        var config = scope.ServiceProvider.GetRequiredService<IAgentConfigService>();
        var clientId = await CreateClientAsync(scope, $"svc-bump-{Guid.NewGuid():N}"[..20]);

        var before = await config.GetRequiredVersionAsync(clientId);

        await admin.CreateAsync(clientId, new CreateMonitoredServiceRequest
        {
            ServiceName = "MSSQLSERVER",
            DisplayName = "SQL Server 数据库引擎",
            ExpectedState = "running"
        });

        var after = await config.GetRequiredVersionAsync(clientId);
        Assert.True(after > before, $"配置版本没有推进（{before} → {after}），新增的监控服务不会下发到客户端");

        // 下发的配置里必须真的带上它，且版本与心跳要求的一致——
        // 两者不一致会让 Agent 每次心跳都重新拉一遍全量配置。
        var payload = await config.GetConfigAsync(clientId, before);
        Assert.Equal(after, payload.Version);
        var service = Assert.Single(payload.MonitoredServices);
        Assert.Equal("MSSQLSERVER", service.ServiceName);
        Assert.Equal("running", service.ExpectedState);
    }

    [Fact]
    public async Task 修改和删除同样推进配置版本()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IMonitoredServiceAdminService>();
        var config = scope.ServiceProvider.GetRequiredService<IAgentConfigService>();
        var clientId = await CreateClientAsync(scope, $"svc-edit-{Guid.NewGuid():N}"[..20]);

        var created = await admin.CreateAsync(clientId, new CreateMonitoredServiceRequest { ServiceName = "W3SVC" });
        var afterCreate = await config.GetRequiredVersionAsync(clientId);

        await admin.UpdateAsync(clientId, created.DefinitionId, new UpdateMonitoredServiceRequest
        {
            DisplayName = "IIS",
            ExpectedState = "stopped",
            AlertOnMismatch = false,
            Enabled = true
        });
        var afterUpdate = await config.GetRequiredVersionAsync(clientId);
        Assert.True(afterUpdate > afterCreate);

        await admin.DeleteAsync(clientId, created.DefinitionId);
        var afterDelete = await config.GetRequiredVersionAsync(clientId);
        Assert.True(afterDelete > afterUpdate);

        Assert.Empty(await admin.ListAsync(clientId));
    }

    /// <summary>
    /// 配置版本必须单调不减。它同时受任务版本和客户端修订号影响，
    /// 任何一边倒退都会让 Agent 认为"配置回到了旧版本"而不再同步。
    /// </summary>
    [Fact]
    public async Task 配置版本在任务与服务变更交替时保持单调()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = scope.ServiceProvider.GetRequiredService<IMonitoredServiceAdminService>();
        var config = scope.ServiceProvider.GetRequiredService<IAgentConfigService>();
        var clientId = await CreateClientAsync(scope, $"svc-mono-{Guid.NewGuid():N}"[..20]);

        var versions = new List<long> { await config.GetRequiredVersionAsync(clientId) };

        await admin.CreateAsync(clientId, new CreateMonitoredServiceRequest { ServiceName = "Spooler" });
        versions.Add(await config.GetRequiredVersionAsync(clientId));

        // 造一个任务，其 config_version 远高于当前修订号
        db.BackupTasks.Add(new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "任务",
            ApplicationName = "SQLServer",
            SourcePath = @"D:\backup",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            Enabled = true,
            ConfigVersion = 100,
            RecognizerConfig = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        versions.Add(await config.GetRequiredVersionAsync(clientId));

        // 再改一次监控服务：修订号必须越过任务版本，而不是退回到 2
        await admin.CreateAsync(clientId, new CreateMonitoredServiceRequest { ServiceName = "Dnscache" });
        versions.Add(await config.GetRequiredVersionAsync(clientId));

        for (var i = 1; i < versions.Count; i++)
            Assert.True(versions[i] >= versions[i - 1],
                $"配置版本倒退：{string.Join(" → ", versions)}");
        Assert.True(versions[^1] > versions[^2], "服务变更后版本必须继续前进");
    }

    [Fact]
    public async Task 同一客户端不允许重复监控同一个服务()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IMonitoredServiceAdminService>();
        var clientId = await CreateClientAsync(scope, $"svc-dup-{Guid.NewGuid():N}"[..20]);

        await admin.CreateAsync(clientId, new CreateMonitoredServiceRequest { ServiceName = "MSSQLSERVER" });

        // 大小写不同也算同一个服务——Windows 服务名本身就是大小写不敏感的。
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            admin.CreateAsync(clientId, new CreateMonitoredServiceRequest { ServiceName = "mssqlserver" }));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task 显示名留空时沿用服务名()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IMonitoredServiceAdminService>();
        var clientId = await CreateClientAsync(scope, $"svc-name-{Guid.NewGuid():N}"[..20]);

        var created = await admin.CreateAsync(clientId, new CreateMonitoredServiceRequest
        {
            ServiceName = "  Dnscache  ",
            DisplayName = "   "
        });

        Assert.Equal("Dnscache", created.ServiceName);
        Assert.Equal("Dnscache", created.DisplayName);
    }

    [Fact]
    public async Task 非法期望状态被拒绝()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IMonitoredServiceAdminService>();
        var clientId = await CreateClientAsync(scope, $"svc-bad-{Guid.NewGuid():N}"[..20]);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            admin.CreateAsync(clientId, new CreateMonitoredServiceRequest
            {
                ServiceName = "Spooler",
                ExpectedState = "whatever"
            }));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task 读取服务列表会下发指令并标出已监控项()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IMonitoredServiceAdminService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clientId = await CreateClientAsync(scope, $"svc-disc-{Guid.NewGuid():N}"[..20]);

        await admin.CreateAsync(clientId, new CreateMonitoredServiceRequest { ServiceName = "MSSQLSERVER" });

        var dispatched = await admin.DispatchListServicesAsync(clientId);
        var command = await db.Commands.FirstAsync(c => c.Id == dispatched.CommandId);
        Assert.Equal(CommandType.ListServices, command.CommandType);

        // 模拟 Agent 回报
        command.Status = CommandStatus.Succeeded;
        command.ResultPayload = """
        {"capturedAt":"2026-08-25T02:00:00Z","services":[
          {"serviceName":"MSSQLSERVER","displayName":"SQL Server","status":"running","startType":"automatic"},
          {"serviceName":"Spooler","displayName":"Print Spooler","status":"stopped","startType":"manual"}]}
        """;
        await db.SaveChangesAsync();

        var result = await admin.GetListServicesResultAsync(dispatched.CommandId);
        Assert.Equal("succeeded", result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal(2, result.Result!.Services.Count);

        // 已经在监控的要标出来，界面据此禁用重复添加——比点了才收到 409 友好。
        Assert.True(result.Result.Services.Single(s => s.ServiceName == "MSSQLSERVER").AlreadyMonitored);
        Assert.False(result.Result.Services.Single(s => s.ServiceName == "Spooler").AlreadyMonitored);
    }
}

/// <summary>测试用空告警服务：这些用例关心的是配置链路，不是告警。</summary>
internal sealed class NoopAlertingService : IAlertingService
{
    public Task RaiseAsync(
        string alertKey, AlertLevel level, string category, string title, string? message = null,
        Guid? clientId = null, Guid? taskId = null, Guid? businessUnitId = null, Guid? backupSetId = null,
        string? metadata = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task RecoverAsync(string alertKey, CancellationToken ct = default) => Task.CompletedTask;
}
