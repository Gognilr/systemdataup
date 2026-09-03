using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Upload;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 手动「立即备份」的两道闸（批次 D1 / D3）。
///
/// D1：幂等键原先拼了当前秒（manual-precheck:{taskId}:{unixSeconds}），只挡得住
/// 同一秒内的重复提交。扫描要跑几分钟，等待窗口被误关之后再点一次就是一条全新指令，
/// 点几次就并发几条。改成稳定键之后：在跑的原样还回去，终结了才复位重下——
/// 「终结后能重下」这一半同样重要，只认「键存在就拒绝」会让任务在第一次备份之后永远点不动。
///
/// D3：系统里本来有两套并发闸（计划执行的 max_concurrent、批量操作的
/// max_concurrent_clients），手动立即备份两套都不走；唯一与上传并发有关的检查是
/// 按客户端的，十个任务在十台不同客户端上时它一次都不会触发。全局闸放在下发侧
/// （SequentialExecutionWorker 放行之前），不放在建会话侧——Agent 对
/// UPLOAD_SESSION_CONFLICT 没有退避重试，在那一步 409 等于把限流变成备份失败。
/// </summary>
[Collection("postgres")]
public class ManualBackupConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public ManualBackupConcurrencyTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

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
        sc.AddScoped<ICommandDispatcher>(sp => sp.GetRequiredService<CommandService>());
        sc.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        sc.AddScoped<IBackupTaskService, BackupTaskService>();
        sc.AddScoped<IUploadProgressService, UploadProgressService>();
        sc.AddSingleton<UploadRateSampler>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- D1 ----------

    [Fact]
    public async Task 连续点十次立即备份只产生一条预检指令()
    {
        var (_, taskId) = await SeedClientTaskAsync();

        for (var i = 0; i < 10; i++)
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckAsync(taskId);
        }

        await using var check = _services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var commands = await db.Commands.AsNoTracking()
            .Where(c => c.TaskId == taskId && c.CommandType == CommandType.PrecheckTask)
            .ToListAsync();

        Assert.Single(commands);
    }

    /// <summary>
    /// 在跑的那一条要原样还回去，并且如实告诉界面「这是先前那一条」——
    /// 装作新发起了一次，人就会以为自己刚点的没生效，转头去点第三次。
    /// </summary>
    [Fact]
    public async Task 已经在跑时返回原指令并标记already_running()
    {
        var (_, taskId) = await SeedClientTaskAsync();

        DispatchCommandResponse first;
        await using (var scope = _services.CreateAsyncScope())
            first = await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckAsync(taskId);
        Assert.Equal("accepted", first.Status);

        // Agent 领走了这条指令
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Commands.Where(c => c.Id == first.CommandId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, CommandStatus.Running)
                    .SetProperty(c => c.StartedAt, DateTime.UtcNow));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var second = await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckAsync(taskId);
            Assert.Equal(first.CommandId, second.CommandId);
            Assert.Equal("already_running", second.Status);
        }
    }

    /// <summary>
    /// 上一条跑完之后再点必须能真的重跑。这一条不通过的话，任务在第一次备份之后
    /// 就永远点不动了——比重复下发更糟。
    /// </summary>
    [Fact]
    public async Task 上一条跑完之后能再发起一次()
    {
        var (_, taskId) = await SeedClientTaskAsync();

        DispatchCommandResponse first;
        await using (var scope = _services.CreateAsyncScope())
            first = await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckAsync(taskId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Commands.Where(c => c.Id == first.CommandId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, CommandStatus.Succeeded)
                    .SetProperty(c => c.CompletedAt, DateTime.UtcNow));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var second = await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckAsync(taskId);
            Assert.Equal(first.CommandId, second.CommandId);   // 同一条被复位，而不是又堆一条
            Assert.Equal("accepted", second.Status);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var command = await db.Commands.AsNoTracking().SingleAsync(c => c.Id == second.CommandId);
            Assert.Equal(CommandStatus.Pending, command.Status);
            Assert.Null(command.CompletedAt);
        }
    }

    /// <summary>「强制完整校验」勾选后重下：payload 必须换成这一次的参数，否则人明确要求的动作被静默忽略。</summary>
    [Fact]
    public async Task 复位重下时payload换成这一次的参数()
    {
        var (_, taskId) = await SeedClientTaskAsync();

        Guid commandId;
        await using (var scope = _services.CreateAsyncScope())
            commandId = (await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckAsync(taskId)).CommandId;

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Commands.Where(c => c.Id == commandId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatus.Succeeded));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckAsync(taskId, forceFullHash: true);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var command = await db.Commands.AsNoTracking().SingleAsync(c => c.Id == commandId);
            Assert.Contains("forceFullHash", command.Payload ?? "");
        }
    }

    // ---------- D3 ----------

    /// <summary>多选批量「立即备份」建一次执行，而不是发 N 条独立指令。</summary>
    [Fact]
    public async Task 批量立即备份建一次执行而不是发N条指令()
    {
        var taskIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
            taskIds.Add((await SeedClientTaskAsync()).TaskId);

        await SetSettingAsync(ExecutionQueueService.ManualMaxConcurrentKey, "2");

        BatchBackupNowResponse response;
        await using (var scope = _services.CreateAsyncScope())
            response = await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckBatchAsync(new BatchBackupNowRequest { TaskIds = taskIds });

        Assert.NotNull(response.ExecutionRunId);
        Assert.Equal(5, response.QueuedTasks);
        Assert.Equal(2, response.MaxConcurrent);

        await using var check = _services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        // 建执行的那一刻一条指令都还没发出去——指令由 SequentialExecutionWorker 在放行时才生成
        Assert.Empty(await db.Commands.AsNoTracking().Where(c => taskIds.Contains(c.TaskId!.Value)).ToListAsync());

        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync(r => r.Id == response.ExecutionRunId);
        Assert.Equal(ExecutionRunKind.Manual, run.Kind);
    }

    /// <summary>
    /// 十台不同客户端同时点「立即备份」：任一时刻全局活动上传会话数不超过
    /// max_concurrent_uploads_total，其余排队。按客户端的那条检查在这里一次都不会触发——
    /// 这正是它管不了的那个场景。
    ///
    /// C1：这条原先播种的是**预检**项，断言「预检项也被上传闸挡住」。
    /// 那个行为本身是缺陷——预检只让客户端扫目录算哈希，一个上传名额都不占，
    /// 而 Pending 项当时没有超时、计划又不允许叠一次执行，于是一挡就是永久的。
    /// 现在播种真实的上传项来验证闸仍然有效，预检项不受闸影响由下面那条单独钉住。
    /// </summary>
    [Fact]
    public async Task 全局上传并发达到上限时不再放行新的上传项()
    {
        var clients = new List<(Guid ClientId, Guid TaskId)>();
        for (var i = 0; i < 4; i++)
            clients.Add(await SeedClientTaskAsync());

        await SetSettingAsync(SequentialExecutionWorker.GlobalUploadLimitKey, "2");

        var runId = await SeedUploadRunAsync(clients);

        // 两台客户端已经在传（各自一个会话，按客户端的检查不会拦——它们不是同一台）
        await SeedActiveUploadAsync(clients[0].ClientId, clients[0].TaskId);
        await SeedActiveUploadAsync(clients[1].ClientId, clients[1].TaskId);

        await RunPassAsync();

        Assert.Equal(0, await CountAsync(runId, ExecutionItemStatus.Running));
        Assert.Equal(4, await CountAsync(runId, ExecutionItemStatus.Pending));

        // 名额腾出来（会话终结）→ 队列继续放行
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.UploadSessions.ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UploadStatus.Committed));
        }

        await RunPassAsync();
        Assert.True(await CountAsync(runId, ExecutionItemStatus.Running) > 0,
            "名额释放之后排队的项必须能开跑，否则限流就变成了饿死");
    }

    /// <summary>
    /// C1：全局上传名额满了，计划/批量里的**预检**项仍然要能开跑。
    ///
    /// 预检只是让客户端扫一遍目录算哈希，一个上传名额都不占。拿上传闸挡住它，
    /// 等于「服务端有 4 个会话在传」就让所有计划连扫描都不许开始；
    /// 而 CreatePlanRunAsync 规定「上一次没跑完就不再建新的」，
    /// 于是一次卡住 = 这个计划永久不再执行，现场表现是它停在「排队中」。
    /// </summary>
    [Fact]
    public async Task 全局上传名额满时预检项仍然能开跑()
    {
        var taskIds = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var (clientId, taskId) = await SeedClientTaskAsync();
            taskIds.Add(taskId);
            await SeedActiveUploadAsync(clientId, taskId);   // 四个活动会话，名额占满
        }

        await SetSettingAsync(SequentialExecutionWorker.GlobalUploadLimitKey, "4");
        await SetSettingAsync(ExecutionQueueService.ManualMaxConcurrentKey, "10");

        Guid runId;
        await using (var scope = _services.CreateAsyncScope())
            runId = (await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckBatchAsync(new BatchBackupNowRequest { TaskIds = taskIds })).ExecutionRunId!.Value;

        await RunPassAsync();

        Assert.Equal(4, await CountAsync(runId, ExecutionItemStatus.Running));
        Assert.Equal(0, await CountAsync(runId, ExecutionItemStatus.Pending));
    }

    /// <summary>上限设为 1：行为退化成串行，且不会死锁——放不出去就等下一轮，而不是判失败。</summary>
    [Fact]
    public async Task 全局上限为一时退化成串行且不判失败()
    {
        var taskIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
            taskIds.Add((await SeedClientTaskAsync()).TaskId);

        await SetSettingAsync(SequentialExecutionWorker.GlobalUploadLimitKey, "1");
        await SetSettingAsync(ExecutionQueueService.ManualMaxConcurrentKey, "10");

        Guid runId;
        await using (var scope = _services.CreateAsyncScope())
            runId = (await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckBatchAsync(new BatchBackupNowRequest { TaskIds = taskIds })).ExecutionRunId!.Value;

        await RunPassAsync();

        // 一个活动会话都还没有 → 第一轮可以放行，但每放行一项都要重查一次全局计数，
        // 因此这里断言的是「没有任何一项被判失败」，以及队列没有一次性全放。
        Assert.Equal(0, await CountAsync(runId, ExecutionItemStatus.Failed));

        await SeedActiveUploadAsync((await FirstClientOfAsync(taskIds[0])), taskIds[0]);
        await RunPassAsync();

        Assert.Equal(0, await CountAsync(runId, ExecutionItemStatus.Failed));
    }

    /// <summary>界面要能看见排队，否则限流的表现就是「点了没反应」——那比不限流更糟。</summary>
    [Fact]
    public async Task 队列状态能查到在传数与排队数()
    {
        var taskIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
            taskIds.Add((await SeedClientTaskAsync()).TaskId);

        await SetSettingAsync(SequentialExecutionWorker.GlobalUploadLimitKey, "4");
        await SetSettingAsync(ExecutionQueueService.ManualMaxConcurrentKey, "1");

        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IBackupTaskService>()
                .DispatchPrecheckBatchAsync(new BatchBackupNowRequest { TaskIds = taskIds });

        await using var check = _services.CreateAsyncScope();
        var queueStatus = await check.ServiceProvider.GetRequiredService<IUploadProgressService>()
            .GetQueueStatusAsync();

        Assert.Equal(4, queueStatus.GlobalLimit);
        Assert.True(queueStatus.QueuedItems >= 3, "刚建出来的三项都还没放行，应当计入排队");
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

    private async Task<int> CountAsync(Guid runId, ExecutionItemStatus status)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ExecutionRunItems.AsNoTracking().CountAsync(i => i.RunId == runId && i.Status == status);
    }

    private async Task<Guid> FirstClientOfAsync(Guid taskId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BackupTasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.ClientId).SingleAsync();
    }

    private async Task SetSettingAsync(string key, string jsonValue)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
            VALUES ({key}, {jsonValue}::jsonb, false, NULL)
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """);

        // SystemSettingsProvider 带缓存，改完要让它重新读一遍，否则测试改的值根本不生效。
        scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>().Invalidate();
    }

    /// <summary>
    /// 播一次「上传」执行：每个任务一个已通过预检的候选 + 一个 UploadCandidate 项。
    /// 上传闸只对这类项生效，验证它就得播真的上传项，而不是预检项。
    /// </summary>
    private async Task<Guid> SeedUploadRunAsync(List<(Guid ClientId, Guid TaskId)> targets)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var run = new Core.Entities.Execution.ExecutionRun
        {
            Id = Guid.NewGuid(),
            Kind = ExecutionRunKind.Manual,
            Name = "上传闸测试",
            Status = BatchStatus.Pending,
            MaxConcurrent = 10,
            ItemTimeoutMinutes = 120,
            TriggerSource = "test",
            CreatedAt = now,
            TotalItems = targets.Count
        };

        var sort = 0;
        foreach (var (clientId, taskId) in targets)
        {
            var candidate = new CandidateBackupSet
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                TaskId = taskId,
                CandidateKey = $"gate-{Guid.NewGuid():N}",
                SourceRoot = @"D:\data",
                DiscoveredAt = now,
                PrecheckStatus = PrecheckStatus.Passed,
                TotalFiles = 1,
                TotalBytes = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.CandidateBackupSets.Add(candidate);

            run.Items.Add(new Core.Entities.Execution.ExecutionRunItem
            {
                Id = Guid.NewGuid(),
                SortOrder = sort++,
                ClientId = clientId,
                TaskId = taskId,
                CandidateBackupSetId = candidate.Id,
                CommandType = CommandType.UploadCandidate,
                Status = ExecutionItemStatus.Pending
            });
        }

        db.ExecutionRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task SeedActiveUploadAsync(Guid clientId, Guid taskId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateKey = $"manual-{Guid.NewGuid():N}",
            SourceRoot = @"D:\data",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.CandidateBackupSets.Add(candidate);
        db.UploadSessions.Add(new UploadSession
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateBackupSetId = candidate.Id,
            Status = UploadStatus.Uploading,
            TotalFiles = 1,
            TotalBytes = 1,
            StartedAt = now,
            LastActivityAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid ClientId, Guid TaskId)> SeedClientTaskAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"manual-{suffix}",
            Hostname = $"manual-host-{suffix[..8]}",
            DisplayName = $"手动备份测试-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"manual-task-{suffix[..8]}",
            ApplicationName = "ManualTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Manual,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return (client.Id, task.Id);
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
