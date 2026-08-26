using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 删除备份任务的回归测试。
///
/// 背景：commands 和 alerts 都以 ON DELETE NO ACTION 指向 backup_tasks。
/// 删除逻辑原先只把 pending 指令改成 cancelled——行仍然留在表里，
/// 于是任何被点过一次预检的任务在删除时都会撞上外键约束，
/// 异常兜底成 500「服务器内部错误，请稍后重试」，而重试永远不会成功。
/// 这条路径此前完全没有测试覆盖：已有测试都在删一个刚建出来、没有任何附属记录的任务。
/// </summary>
[Collection("postgres")]
public class BackupTaskDeletionTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public BackupTaskDeletionTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        // 删除路径不下发指令，桩件即可；引真实实现会把 CommandSigner 等一串依赖拖进来。
        sc.AddScoped<ICommandDispatcher, UnusedCommandDispatcher>();
        sc.AddScoped<IAgentConfigService, AgentConfigService>();
        sc.AddScoped<SystemSettingsProvider>();
        sc.AddScoped<IBackupTaskService, BackupTaskService>();
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<(Guid ClientId, Guid TaskId)> CreateTaskAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = Guid.NewGuid().ToString("N"),
            Hostname = "TASKDEL-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "删除测试客户端",
            Status = ClientStatus.Online
        };
        db.Clients.Add(client);

        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = "删除测试任务",
            ApplicationName = "SQLServer",
            SourcePath = @"D:\backup\test",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            Enabled = true
        };
        db.BackupTasks.Add(task);

        await db.SaveChangesAsync();
        return (client.Id, task.Id);
    }

    /// <summary>
    /// 核心回归：任务有历史指令时必须能删掉。
    /// 修复前这里会抛 DbUpdateException（外键 23503），最终被兜成 500。
    /// </summary>
    [Fact]
    public async Task 有历史指令的任务可以删除()
    {
        var (clientId, taskId) = await CreateTaskAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 三种终态各来一条：光取消 pending 是不够的，已完成的指令一样钉着任务。
            foreach (var status in new[] { CommandStatus.Pending, CommandStatus.Succeeded, CommandStatus.Failed })
            {
                db.Commands.Add(new Command
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    TaskId = taskId,
                    CommandType = CommandType.PrecheckTask,
                    Status = status,
                    Nonce = Guid.NewGuid().ToString("N"),
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                });
            }

            await db.SaveChangesAsync();
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var tasks = scope.ServiceProvider.GetRequiredService<IBackupTaskService>();
            await tasks.DeleteAsync(taskId);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.BackupTasks.AnyAsync(t => t.Id == taskId));
            Assert.False(await db.Commands.AnyAsync(c => c.TaskId == taskId));
        }
    }

    /// <summary>告警同样以外键钉着任务，触发过告警的任务也必须能删。</summary>
    [Fact]
    public async Task 有历史告警的任务可以删除()
    {
        var (clientId, taskId) = await CreateTaskAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
            await alerting.RaiseAsync(
                $"test:task-delete:{taskId:N}", AlertLevel.Warning, "unit_test", "删除测试告警", "正文",
                clientId: clientId, taskId: taskId);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.Alerts.AnyAsync(a => a.TaskId == taskId));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupTaskService>().DeleteAsync(taskId);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.BackupTasks.AnyAsync(t => t.Id == taskId));
            Assert.False(await db.Alerts.AnyAsync(a => a.TaskId == taskId));
        }
    }

    /// <summary>
    /// 候选备份集仍然必须挡住删除，并且要给出 409 和能读懂的原因——
    /// 「删不掉」和「服务器故障」是两回事，放宽指令清理不能把这条守卫一起放掉。
    /// </summary>
    [Fact]
    public async Task 有候选备份集的任务仍然拒绝删除()
    {
        var (clientId, taskId) = await CreateTaskAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CandidateBackupSets.Add(new CandidateBackupSet
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                TaskId = taskId,
                CandidateKey = "cand-" + Guid.NewGuid().ToString("N"),
                SourceRoot = @"D:\backup\test",
                DiscoveredAt = DateTime.UtcNow,
                PrecheckStatus = PrecheckStatus.Passed
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var tasks = scope.ServiceProvider.GetRequiredService<IBackupTaskService>();
            var ex = await Assert.ThrowsAsync<BusinessException>(() => tasks.DeleteAsync(taskId));
            Assert.Equal(409, ex.StatusCode);
        }
    }
}

/// <summary>删除路径不该下发任何指令；真被调用说明测试假设已经不成立。</summary>
internal sealed class UnusedCommandDispatcher : ICommandDispatcher
{
    public Task<BackupMonitor.Core.Entities.Backup.Command> CreateCommandAsync(
        Guid clientId,
        CommandType commandType,
        Guid? taskId = null,
        Guid? candidateBackupSetId = null,
        object? payload = null,
        int priority = 100,
        TimeSpan? ttl = null,
        string? idempotencyKey = null,
        Guid? createdBy = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("删除备份任务不应下发指令");
}
