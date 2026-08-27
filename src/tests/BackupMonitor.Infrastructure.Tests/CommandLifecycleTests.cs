using System.Text.Json;
using BackupMonitor.Core.Entities.Backup;
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
/// 指令生命周期与结果收口集成测试（审计 D-05 / D-06）。
///
/// D-05：commands.expired 这一格状态零处写入，claimed/running 没有任何时间驱动的出口。
/// Agent 在领取和回报之间挂掉，管理端就多一条永不结束的「正在执行」。
/// D-06：result_message 是 varchar(2000) 而 EF 的 HasMaxLength 运行时不截断，
/// 一条带完整路径的 IOException 就能让接口 500，把 Agent 逼进「再报一次、再 500」的死循环。
/// </summary>
[Collection("postgres")]
public class CommandLifecycleTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public CommandLifecycleTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

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
        sc.AddScoped<IUploadStorage, UploadStorage>();
        sc.AddSingleton<CommandSigner>();
        sc.AddScoped<CommandService>();
        sc.AddScoped<IAgentCommandService>(sp => sp.GetRequiredService<CommandService>());
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- D-05 ----------

    [Fact]
    public async Task 领取后失联的指令退回待领取()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();

        // 领取时刻早于 command_claim_timeout_seconds（默认 900 秒），但 TTL 还没到
        var stale = await SeedCommandAsync(clientId, taskId, CommandStatus.Claimed,
            claimedAt: DateTime.UtcNow.AddHours(-1), expiresAt: DateTime.UtcNow.AddHours(6));

        // 刚领走的不能碰——Agent 可能正在准备开工
        var fresh = await SeedCommandAsync(clientId, taskId, CommandStatus.Claimed,
            claimedAt: DateTime.UtcNow, expiresAt: DateTime.UtcNow.AddHours(6));

        await RunPassAsync();

        var recycled = await LoadAsync(stale);
        Assert.Equal(CommandStatus.Pending, recycled.Status);
        Assert.Null(recycled.ClaimedAt);   // 清空才能被 ClaimAsync 重新领走

        Assert.Equal(CommandStatus.Claimed, (await LoadAsync(fresh)).Status);
    }

    [Fact]
    public async Task 已过期的指令一律置过期而未过期的运行中不被碰()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();

        var expiredRunning = await SeedCommandAsync(clientId, taskId, CommandStatus.Running,
            claimedAt: DateTime.UtcNow.AddHours(-8), expiresAt: DateTime.UtcNow.AddHours(-1));
        var expiredPending = await SeedCommandAsync(clientId, taskId, CommandStatus.Pending,
            claimedAt: null, expiresAt: DateTime.UtcNow.AddHours(-1));

        // running 且未过期：可能正在传几十 GB，退回或过期都会导致重复执行
        var liveRunning = await SeedCommandAsync(clientId, taskId, CommandStatus.Running,
            claimedAt: DateTime.UtcNow.AddHours(-8), expiresAt: DateTime.UtcNow.AddHours(6));

        await RunPassAsync();

        var recycled = await LoadAsync(expiredRunning);
        Assert.Equal(CommandStatus.Expired, recycled.Status);
        Assert.Equal("COMMAND_EXPIRED", recycled.ResultCode);
        Assert.NotNull(recycled.CompletedAt);

        Assert.Equal(CommandStatus.Expired, (await LoadAsync(expiredPending)).Status);
        Assert.Equal(CommandStatus.Running, (await LoadAsync(liveRunning)).Status);
    }

    // ---------- D-06 ----------

    [Fact]
    public async Task 超长结果消息被截断而不是让接口炸掉()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedCommandAsync(clientId, taskId, CommandStatus.Running,
            claimedAt: DateTime.UtcNow, expiresAt: DateTime.UtcNow.AddHours(6));

        await using (var scope = _services.CreateAsyncScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAgentCommandService>();
            await svc.ReportCompletedAsync(clientId, commandId, new CommandCompletedRequest
            {
                Success = false,
                ResultCode = new string('C', 200),
                ResultMessage = new string('长', 5000)
            });
        }

        var command = await LoadAsync(commandId);
        Assert.Equal(CommandStatus.Failed, command.Status);
        Assert.Equal(64, command.ResultCode!.Length);
        Assert.Equal(1900, command.ResultMessage!.Length);
    }

    [Fact]
    public async Task 非法_JSON_结果被替换成摘要而不是_500()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedCommandAsync(clientId, taskId, CommandStatus.Running,
            claimedAt: DateTime.UtcNow, expiresAt: DateTime.UtcNow.AddHours(6));

        await using (var scope = _services.CreateAsyncScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAgentCommandService>();
            // 非法 JSON 进 jsonb 列会直接 500，Agent 的 catch 分支再报一次、再 500
            await svc.ReportCompletedAsync(clientId, commandId, new CommandCompletedRequest
            {
                Success = true,
                ResultCode = "OK",
                Result = "not json"
            });
        }

        var command = await LoadAsync(commandId);
        Assert.Equal(CommandStatus.Succeeded, command.Status);   // 指令本身是成功的，坏的只是结果呈现
        using var doc = JsonDocument.Parse(command.ResultPayload!);
        Assert.True(doc.RootElement.GetProperty("invalidJson").GetBoolean());
    }

    [Fact]
    public async Task 超大结果载荷被替换成摘要()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedCommandAsync(clientId, taskId, CommandStatus.Running,
            claimedAt: DateTime.UtcNow, expiresAt: DateTime.UtcNow.AddHours(6));

        // 合法 JSON，但远超兜底上限（2MB）
        var huge = JsonSerializer.Serialize(new { blob = new string('x', 3 * 1024 * 1024) });

        await using (var scope = _services.CreateAsyncScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAgentCommandService>();
            await svc.ReportCompletedAsync(clientId, commandId, new CommandCompletedRequest
            {
                Success = true,
                ResultCode = "OK",
                Result = huge
            });
        }

        var command = await LoadAsync(commandId);
        using var doc = JsonDocument.Parse(command.ResultPayload!);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    // ---------- 基础设施 ----------

    private async Task RunPassAsync()
    {
        var worker = new LifecycleExpiryWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<LifecycleExpiryWorker>>());
        using var scope = _services.CreateScope();
        await worker.RunPassAsync(scope, CancellationToken.None);
    }

    private async Task<Command> LoadAsync(Guid commandId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Commands.AsNoTracking().FirstAsync(c => c.Id == commandId);
    }

    private async Task<Guid> SeedCommandAsync(
        Guid clientId, Guid taskId, CommandStatus status, DateTime? claimedAt, DateTime expiresAt)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var signer = scope.ServiceProvider.GetRequiredService<CommandSigner>();

        var command = new Command
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CommandType = CommandType.RefreshMetrics,
            Status = status,
            Priority = 100,
            Nonce = Guid.NewGuid().ToString("N"),
            ClaimedAt = claimedAt,
            ExpiresAt = expiresAt
        };
        command.Signature = signer.SignCommand(command);

        db.Commands.Add(command);
        await db.SaveChangesAsync();
        return command.Id;
    }

    private async Task<(Guid ClientId, Guid TaskId)> SeedClientTaskAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"itcl-{suffix}",
            Hostname = $"itcl-host-{suffix[..8]}",
            DisplayName = $"指令测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"itcl-task-{suffix[..8]}",
            ApplicationName = "CommandTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return (client.Id, task.Id);
    }

    /// <summary>CommandSigner 需要一把真实的 RSA 私钥才能构造</summary>
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
