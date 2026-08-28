using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 新建任务默认绑定保留策略（V027）。
///
/// 背景：清理器只处理 retention_policy_id 非空的任务，而这一项在任务表单的高级折叠区里
/// 默认留空——两件事凑一起，结果是按默认值建出来的任务永远不清理、仓库无限增长。
/// 「留空 = 永不清理」这个默认值是反的，因此服务端在建任务时兜底绑上默认策略。
/// </summary>
[Collection("postgres")]
public class RetentionDefaultBindingTests : IAsyncLifetime
{
    /// <summary>V027 预置的「只留最近 7 份」，也是 default_retention_policy_id 的种子值</summary>
    private static readonly Guid SeedDefaultPolicyId = Guid.Parse("d0000002-0000-0000-0000-000000000007");

    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public RetentionDefaultBindingTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

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
        sc.AddScoped<IAgentConfigService, AgentConfigService>();
        sc.AddScoped<SystemSettingsProvider>();
        sc.AddScoped<IBackupTaskService, BackupTaskService>();
        sc.AddScoped<IRetentionPolicyService, RetentionPolicyService>();
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>没选保留策略 → 绑上默认策略，而不是留空（= 永不清理）</summary>
    [Fact]
    public async Task 建任务不选策略时绑上默认策略()
    {
        var clientId = await SeedClientAsync();

        await using var scope = _services.CreateAsyncScope();
        var tasks = scope.ServiceProvider.GetRequiredService<IBackupTaskService>();
        var detail = await tasks.CreateAsync(NewRequest(clientId));

        Assert.Equal(SeedDefaultPolicyId, detail.RetentionPolicyId);
    }

    /// <summary>显式选了别的策略 → 按人选的来，兜底不能覆盖它</summary>
    [Fact]
    public async Task 显式指定的策略不会被默认策略覆盖()
    {
        var clientId = await SeedClientAsync();
        var chosen = Guid.Parse("d0000002-0000-0000-0000-000000000003");   // 只留最近 3 份

        await using var scope = _services.CreateAsyncScope();
        var tasks = scope.ServiceProvider.GetRequiredService<IBackupTaskService>();
        var request = NewRequest(clientId);
        request.RetentionPolicyId = chosen;
        var detail = await tasks.CreateAsync(request);

        Assert.Equal(chosen, detail.RetentionPolicyId);
    }

    /// <summary>默认策略是新建任务的兜底，被任务引用之外还要挡住「删掉它」</summary>
    [Fact]
    public async Task 默认策略不能被删除()
    {
        await using var scope = _services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRetentionPolicyService>();

        var ex = await Assert.ThrowsAsync<BackupMonitor.Shared.Exceptions.BusinessException>(
            () => policies.DeleteAsync(SeedDefaultPolicyId));
        Assert.Equal(409, ex.StatusCode);
    }

    /// <summary>预置策略必须真的装进库里，否则任务表单里还是只有那份 GFS</summary>
    [Fact]
    public async Task 预置策略随迁移装好且默认策略被标出()
    {
        await using var scope = _services.CreateAsyncScope();
        var policies = await scope.ServiceProvider.GetRequiredService<IRetentionPolicyService>().GetListAsync();

        Assert.Contains(policies, p => p.Name == "只留最近 3 份" && p.KeepLastCount == 3);
        Assert.Contains(policies, p => p.Name == "只留最近 7 份" && p.KeepLastCount == 7);
        Assert.Contains(policies, p => p.Name == "日备留 7 天 + 月末留 12 个月" && p.KeepMonthlyCount == 12);

        // 最短保留天数会盖过份数设置，预置策略一律 0
        Assert.All(policies.Where(p => p.Id.ToString().StartsWith("d0000002")),
            p => Assert.Equal(0, p.MinimumRetentionDays));

        var single = Assert.Single(policies, p => p.IsDefault);
        Assert.Equal(SeedDefaultPolicyId, single.Id);
    }

    private async Task<Guid> SeedClientAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = Guid.NewGuid().ToString("N"),
            Hostname = "RETDEF-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "默认策略测试客户端",
            Status = ClientStatus.Online
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static CreateBackupTaskRequest NewRequest(Guid clientId) => new()
    {
        ClientId = clientId,
        Name = "默认策略测试任务-" + Guid.NewGuid().ToString("N")[..8],
        ApplicationName = "SQLServer",
        SourcePath = @"D:\backup\test",
        RecognizerType = "multi_file_set",
        TaskMode = "automatic"
    };
}
