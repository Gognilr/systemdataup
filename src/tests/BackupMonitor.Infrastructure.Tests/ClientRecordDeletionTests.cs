using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 已注销机器的去留。
///
/// 一台机器重装之后会重新登记成新的一条，旧的那条注销掉——于是列表里出现两条同名机器，
/// 新建备份任务的选择框里也是两条，选错的那一条建出来的任务永远不会执行。
/// 两件事一起解决：默认不列已注销的，以及给它们一个真正的删除。
///
/// 删除刻意是有前提的：名下还有备份任务时拒绝。任务删除会牵动备份集和仓库目录里的
/// 真实文件，那套判断在 BackupTaskService 里，这里要是绕过去，「删机器」就成了
/// 一条绕开备份保护的近路。
/// </summary>
[Collection("postgres")]
public class ClientRecordDeletionTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public ClientRecordDeletionTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton(SigningConfiguration());
        sc.AddSingleton<CommandSigner>();
        sc.AddSingleton<CertificateAuthority>();
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<ICommandDispatcher, CommandService>();
        sc.AddScoped<IClientAdminService, ClientAdminService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task 列表默认不含已注销的机器()
    {
        var online = await SeedClientAsync(ClientStatus.Online);
        var revoked = await SeedClientAsync(ClientStatus.Revoked);

        var page = await ListAsync(new ClientQuery { Page = 1, PageSize = 500 });

        Assert.Contains(page.Items, i => i.Id == online);
        // 新建任务的机器选择框走的就是这个默认口径：注销的那条不该出现在候选里。
        Assert.DoesNotContain(page.Items, i => i.Id == revoked);
    }

    [Fact]
    public async Task 显式要求时才把已注销的一起列出来()
    {
        var revoked = await SeedClientAsync(ClientStatus.Revoked);

        var all = await ListAsync(new ClientQuery { Page = 1, PageSize = 500, IncludeRevoked = true });
        Assert.Contains(all.Items, i => i.Id == revoked);

        // 按状态筛「已注销」是另一条路：它本身就说明了意图，不该再被开关挡一次。
        var only = await ListAsync(new ClientQuery { Page = 1, PageSize = 500, Status = "revoked" });
        Assert.Contains(only.Items, i => i.Id == revoked);
        Assert.All(only.Items, i => Assert.Equal("revoked", i.Status));
    }

    [Fact]
    public async Task 在用的机器不能删除记录()
    {
        var online = await SeedClientAsync(ClientStatus.Online);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => DeleteAsync(online));
        Assert.Equal(409, ex.StatusCode);

        // 删不掉就必须还在：半截删除比删不掉严重得多
        Assert.True(await ExistsAsync(online));
    }

    [Fact]
    public async Task 名下还有备份任务时拒绝删除()
    {
        var clientId = await SeedClientAsync(ClientStatus.Revoked);
        await SeedTaskAsync(clientId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => DeleteAsync(clientId));
        Assert.Equal(409, ex.StatusCode);
        // 提示要指出下一步去哪做，而不是只说一句「不行」
        Assert.Contains("备份任务", ex.Message);

        Assert.True(await ExistsAsync(clientId));
    }

    [Fact]
    public async Task 已注销且没有任务时连同附属记录一起删掉()
    {
        var clientId = await SeedClientAsync(ClientStatus.Revoked);
        await SeedClientScopedRowsAsync(clientId);

        await DeleteAsync(clientId);

        Assert.False(await ExistsAsync(clientId));

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // 这几张表对 clients 都是 NO ACTION：漏掉任何一张，删除会以外键异常收场，
        // 界面上看到的就是一句「服务器内部错误」。
        Assert.False(await db.ClientHeartbeats.AnyAsync(h => h.ClientId == clientId));
        Assert.False(await db.Commands.AnyAsync(c => c.ClientId == clientId));
        Assert.False(await db.Alerts.AnyAsync(a => a.ClientId == clientId));
        // 证书是 ON DELETE CASCADE，由数据库收尾——这里确认那条级联真的生效了
        Assert.False(await db.ClientCertificates.AnyAsync(c => c.ClientId == clientId));
    }

    // ---------- 基础设施 ----------

    private async Task<Shared.Models.PagedResult<ClientListItemDto>> ListAsync(ClientQuery query)
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientAdminService>();
        return await svc.GetListAsync(query);
    }

    private async Task DeleteAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientAdminService>();
        await svc.DeleteAsync(clientId);
    }

    private async Task<bool> ExistsAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId);
    }

    private async Task<Guid> SeedClientAsync(ClientStatus status)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"del-{suffix}",
            Hostname = $"del-host-{suffix[..8]}",
            DisplayName = $"删除测试客户端-{suffix[..8]}",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private async Task SeedTaskAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        db.BackupTasks.Add(new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = $"del-task-{Guid.NewGuid():N}"[..24],
            ApplicationName = "DelTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    /// <summary>造几条只挂在客户端上（不经过任务）的附属记录：心跳、无任务的指令、机器级告警、证书。</summary>
    private async Task SeedClientScopedRowsAsync(Guid clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        db.ClientHeartbeats.Add(new ClientHeartbeat
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ReceivedAt = now,
            ClientTime = now
        });
        db.Commands.Add(new Command
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CommandType = CommandType.RefreshMetrics,
            Status = CommandStatus.Pending,
            // 指令行带签名字段（nonce 非空）：这里只是要一条真实存在的行，随便给一个即可
            Nonce = Guid.NewGuid().ToString("N"),
            Priority = 50,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        });
        db.Alerts.Add(new Core.Entities.Alert.Alert
        {
            Id = Guid.NewGuid(),
            AlertKey = $"client:{clientId}:offline",
            ClientId = clientId,
            Level = AlertLevel.Warning,
            Category = "client_offline",
            Title = "客户端离线",
            Status = AlertStatus.Open,
            FirstOccurredAt = now,
            LastOccurredAt = now
        });
        db.ClientCertificates.Add(new ClientCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            SerialNumber = Guid.NewGuid().ToString("N"),
            Thumbprint = Guid.NewGuid().ToString("N"),
            Status = CertificateStatus.Revoked,
            IssuedAt = now.AddDays(-1),
            ExpiresAt = now.AddDays(364),
            RevokedAt = now
        });
        await db.SaveChangesAsync();
    }

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
}
