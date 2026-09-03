using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Execution;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 顺序执行器（V028）集成测试：备份计划与批量上传共用的那道并发闸。
///
/// 三条覆盖的正是方案 A / B 的验收标准：
///   并发度 1 时一次只放行一项（而不是全部同时开跑）；
///   卡住的一项超时之后队列继续往下走（一台关机的客户端不能拖死整晚的计划）；
///   并发度 N 时同时在跑的不超过 N（批量上传的并发闸终于名副其实）。
/// </summary>
[Collection("postgres")]
public class SequentialExecutionWorkerTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public SequentialExecutionWorkerTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        // 指令下发用桩件：真实实现要拖进签名器与一串配置，而这里被测的是队列的放行逻辑，
        // 不是指令怎么签名。桩件仍然往 commands 表写真行，终结判定读的就是它。
        sc.AddScoped<ICommandDispatcher, QueueCommandDispatcher>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- 用例一：并发度 1 = 一个接一个 ----------

    [Fact]
    public async Task 并发度1时一次只放行一项()
    {
        var (planId, taskIds) = await SeedPlanAsync(taskCount: 3, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);

        await RunPassAsync();
        Assert.Equal([ExecutionItemStatus.Running, ExecutionItemStatus.Pending, ExecutionItemStatus.Pending],
            await ItemStatusesAsync(runId));

        // 第一项跑完（指令成功 + 会话入库）→ 第二项才被放行
        await CompleteItemAsync(runId, 0, taskIds[0]);
        await RunPassAsync();
        Assert.Equal([ExecutionItemStatus.Succeeded, ExecutionItemStatus.Running, ExecutionItemStatus.Pending],
            await ItemStatusesAsync(runId));

        await CompleteItemAsync(runId, 1, taskIds[1]);
        await CompleteItemAsync(runId, 2, taskIds[2], startFirst: true);
        await RunPassAsync();

        Assert.Equal([ExecutionItemStatus.Succeeded, ExecutionItemStatus.Succeeded, ExecutionItemStatus.Succeeded],
            await ItemStatusesAsync(runId));

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        Assert.Equal(BatchStatus.Completed, run.Status);
        Assert.Equal(3, run.SucceededItems);
        Assert.NotNull(run.FinishedAt);
    }

    // ---------- 用例二：卡住的一项不能拖死整个计划 ----------

    [Fact]
    public async Task 卡住的一项超时后队列继续往下走()
    {
        var (planId, taskIds) = await SeedPlanAsync(taskCount: 2, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);

        await RunPassAsync();

        // 客户端关机：指令一直是 pending，永远不会有结果。
        // 把这一项的开始时间往前推到超时线之外，等价于真的等了那么久。
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ExecutionRunItems
                .Where(i => i.RunId == runId && i.SortOrder == 0)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.StartedAt, DateTime.UtcNow.AddHours(-5)));
        }

        await RunPassAsync();

        var statuses = await ItemStatusesAsync(runId);
        Assert.Equal(ExecutionItemStatus.Timeout, statuses[0]);
        Assert.Equal(ExecutionItemStatus.Running, statuses[1]);   // 第二项照常开跑

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(
                await db.Alerts.AsNoTracking().AnyAsync(a => a.Category == "execution_item_timeout" && a.TaskId == taskIds[0]),
                "超时要发告警——否则没人知道那台机器今晚没备份");
        }

        await CompleteItemAsync(runId, 1, taskIds[1]);
        await RunPassAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.ExecutionRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
            Assert.Equal(BatchStatus.Partial, run.Status);
            Assert.Equal(1, run.SucceededItems);
            Assert.Equal(1, run.FailedItems);
        }
    }

    // ---------- 用例三：并发度 N 就是最多 N 个同时在跑 ----------

    [Fact]
    public async Task 并发度2时同时在跑的不超过两项()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 5, maxConcurrent: 2);
        var runId = await TriggerAsync(planId);

        await RunPassAsync();
        var statuses = await ItemStatusesAsync(runId);
        Assert.Equal(2, statuses.Count(s => s == ExecutionItemStatus.Running));
        Assert.Equal(3, statuses.Count(s => s == ExecutionItemStatus.Pending));

        // 再推一轮也不会多放：闸门看的是「在跑的有几个」，不是「推了几轮」
        await RunPassAsync();
        Assert.Equal(2, (await ItemStatusesAsync(runId)).Count(s => s == ExecutionItemStatus.Running));
    }

    // ---------- 用例四：停用的任务不参与，但要留下痕迹 ----------

    [Fact]
    public async Task 停用的任务被跳过并写明原因()
    {
        var (planId, taskIds) = await SeedPlanAsync(taskCount: 2, maxConcurrent: 1);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BackupTasks.Where(t => t.Id == taskIds[0])
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Enabled, false));
        }

        var runId = await TriggerAsync(planId);
        await RunPassAsync();

        await using var scope2 = _services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var items = await db2.ExecutionRunItems.AsNoTracking()
            .Where(i => i.RunId == runId).OrderBy(i => i.SortOrder).ToListAsync();

        Assert.Equal(ExecutionItemStatus.Skipped, items[0].Status);
        Assert.Equal("任务已停用", items[0].Message);
        Assert.Equal(ExecutionItemStatus.Running, items[1].Status);
    }

    // ---------- 用例五：到点自动执行 ----------

    /// <summary>
    /// 计划的核心：到点了要自己产生一次执行，而且同一个时刻只产生一次
    /// （否则每 15 秒推一轮就会建一次）。
    /// </summary>
    [Fact]
    public async Task 到点的计划自动产生一次执行且不重复()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 2, maxConcurrent: 1);

        // 执行时刻设成一小时前，且从未执行过 → 本轮就该到点
        var runAt = DateTime.UtcNow.AddHours(-1).TimeOfDay;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BackupPlans.Where(p => p.Id == planId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.RunAt, new TimeSpan(runAt.Hours, runAt.Minutes, 0))
                    .SetProperty(p => p.LastRunAt, (DateTime?)null));
        }

        await RunPassAsync();
        await RunPassAsync();

        await using var scope2 = _services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await db2.ExecutionRuns.AsNoTracking().Where(r => r.PlanId == planId).ToListAsync();

        Assert.Single(runs);
        Assert.Equal("schedule", runs[0].TriggerSource);
        Assert.NotNull(runs[0].ScheduledFor);
    }

    /// <summary>
    /// D10：到点了但产生不出执行时（上一次还没跑完），也要把 last_run_at 推到这一次的时刻。
    ///
    /// 不推的话 PromoteDuePlansAsync 的判据（last_run_at 早于 due）一直成立：
    /// 执行器每 15 秒重算一次、重新走到这里、重新打一条同样的日志，一整晚刷几千行。
    /// 更麻烦的是第二个后果——卡住的那次执行一结束，同一个到期时刻立刻被补跑一次，
    /// 而那个时刻可能已经是白天的业务高峰了。
    /// </summary>
    [Fact]
    public async Task 上一次没跑完时到期时刻也要标记为已处理()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 2, maxConcurrent: 1);

        // 先造一次「还没跑完」的执行占住这个计划
        await TriggerAsync(planId);

        var due = DateTime.UtcNow.AddHours(-1);
        await using (var scope = _services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueueService>();
            var run = await queue.CreatePlanRunAsync(planId, due, "schedule", null);
            Assert.Null(run);   // 上一次还没跑完，这一次不该叠一条
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var plan = await db.BackupPlans.AsNoTracking().SingleAsync(p => p.Id == planId);
            Assert.NotNull(plan.LastRunAt);
            Assert.True(plan.LastRunAt >= due,
                "这一次到期已经处理过了（虽然没跑），last_run_at 必须跟上，否则每一轮都会重来一遍");
        }
    }

    /// <summary>手动「立即执行」传的 scheduledFor 是 null，它不该影响下一次到点的判定。</summary>
    [Fact]
    public async Task 手动触发失败不影响到点判定()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 2, maxConcurrent: 1);
        await TriggerAsync(planId);

        DateTime? before;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            before = (await db.BackupPlans.AsNoTracking().SingleAsync(p => p.Id == planId)).LastRunAt;
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueueService>();
            Assert.Null(await queue.CreatePlanRunAsync(planId, null, "manual", null));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var after = (await db.BackupPlans.AsNoTracking().SingleAsync(p => p.Id == planId)).LastRunAt;
            Assert.Equal(before, after);
        }
    }

    // ---------- 基础设施 ----------

    private async Task RunPassAsync()
    {
        var worker = new SequentialExecutionWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<SequentialExecutionWorker>>());
        using var scope = _services.CreateScope();
        await worker.RunPassAsync(scope, CancellationToken.None);
    }

    private async Task<List<ExecutionItemStatus>> ItemStatusesAsync(Guid runId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ExecutionRunItems.AsNoTracking()
            .Where(i => i.RunId == runId)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.Status)
            .ToListAsync();
    }

    private async Task<Guid> TriggerAsync(Guid planId)
    {
        await using var scope = _services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueueService>();
        var run = await queue.CreatePlanRunAsync(planId, null, "manual", null);
        Assert.NotNull(run);
        return run!.Id;
    }

    /// <summary>把某一项推到终结：指令成功 + 该任务产生一个已入库的上传会话</summary>
    private async Task CompleteItemAsync(Guid runId, int sortOrder, Guid taskId, bool startFirst = false)
    {
        if (startFirst)
            await RunPassAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var item = await db.ExecutionRunItems.SingleAsync(i => i.RunId == runId && i.SortOrder == sortOrder);
        Assert.NotNull(item.CommandId);

        var now = DateTime.UtcNow;
        await db.Commands.Where(c => c.Id == item.CommandId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CommandStatus.Succeeded)
                .SetProperty(c => c.CompletedAt, now));

        var task = await db.BackupTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = task.ClientId,
            TaskId = taskId,
            CandidateKey = $"seq-{Guid.NewGuid():N}",
            SourceRoot = @"D:\data",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.CandidateBackupSets.Add(candidate);
        db.UploadSessions.Add(new Core.Entities.Upload.UploadSession
        {
            Id = Guid.NewGuid(),
            ClientId = task.ClientId,
            TaskId = taskId,
            CandidateBackupSetId = candidate.Id,
            Status = UploadStatus.Committed,
            TotalFiles = 1,
            TotalBytes = 1,
            StartedAt = now,
            LastActivityAt = now,
            CompletedAt = now,
            CommittedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid PlanId, List<Guid> TaskIds)> SeedPlanAsync(int taskCount, int maxConcurrent)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var plan = new BackupPlan
        {
            Id = Guid.NewGuid(),
            Name = $"seq-plan-{Guid.NewGuid():N}",
            Enabled = true,
            ScheduleKind = PlanScheduleKind.Daily,
            RunAt = new TimeSpan(2, 0, 0),
            Timezone = "UTC",
            MaxConcurrent = maxConcurrent,
            ItemTimeoutMinutes = 240,
            // 到点判定不参与本测试：这里全部用「立即执行一次」触发，
            // 把 LastRunAt 设成现在可以顺便挡住 PromoteDuePlans 另外再建一次。
            LastRunAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.BackupPlans.Add(plan);

        var taskIds = new List<Guid>();
        for (var i = 0; i < taskCount; i++)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var client = new Client
            {
                Id = Guid.NewGuid(),
                MachineId = $"seq-{suffix}",
                Hostname = $"seq-host-{suffix[..8]}",
                DisplayName = $"顺序执行测试-{suffix[..8]}",
                Status = ClientStatus.Online,
                CreatedAt = now,
                UpdatedAt = now
            };
            var task = new BackupTask
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                Name = $"seq-task-{suffix[..8]}",
                ApplicationName = "SeqTest",
                SourcePath = @"D:\data",
                RecognizerType = RecognizerType.MultiFileSet,
                TaskMode = TaskMode.Automatic,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Clients.Add(client);
            db.BackupTasks.Add(task);
            db.BackupPlanItems.Add(new BackupPlanItem
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                TaskId = task.Id,
                SortOrder = i
            });
            taskIds.Add(task.Id);
        }

        await db.SaveChangesAsync();
        return (plan.Id, taskIds);
    }
}

/// <summary>把指令真的写进 commands 表的桩件（不签名、不做幂等复位以外的事）</summary>
internal sealed class QueueCommandDispatcher : ICommandDispatcher
{
    private readonly AppDbContext _db;

    public QueueCommandDispatcher(AppDbContext db) => _db = db;

    public async Task<Command> CreateCommandAsync(
        Guid clientId,
        CommandType commandType,
        Guid? taskId = null,
        Guid? candidateBackupSetId = null,
        object? payload = null,
        int priority = 100,
        TimeSpan? ttl = null,
        string? idempotencyKey = null,
        Guid? createdBy = null,
        bool restartIfNotActive = false,
        CancellationToken ct = default)
    {
        if (idempotencyKey is not null)
        {
            var existing = await _db.Commands.FirstOrDefaultAsync(c => c.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return existing;
        }

        var now = DateTime.UtcNow;
        var command = new Command
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateBackupSetId = candidateBackupSetId,
            CommandType = commandType,
            Status = CommandStatus.Pending,
            Priority = priority,
            Nonce = Guid.NewGuid().ToString("N"),
            IdempotencyKey = idempotencyKey,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
        _db.Commands.Add(command);
        await _db.SaveChangesAsync(ct);
        return command;
    }
}
