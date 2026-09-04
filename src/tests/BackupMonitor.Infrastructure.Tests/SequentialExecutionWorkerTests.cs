using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Execution;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
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

    // ---------- 用例四：排队兜底超时不能误杀「慢但在推进」的执行 ----------

    /// <summary>
    /// Pending 项的兜底超时只该杀「队列不动了」，不该杀「跑得慢」。
    ///
    /// 这道兜底是为了防止一次卡住的执行让计划永久不再执行（CreatePlanRunAsync 见到
    /// 未终结的 run 就返回 null）。但它最初把「等了多久」从整次执行的开始时刻算起，
    /// 而且不看有没有项在跑——计划默认单项超时 240 分钟、阈值因此是 8 小时，
    /// 而严格顺序的计划挂 5 个 U8 级任务（单个扫描+哈希+上传 2~3 小时）
    /// 串行跑满 12 小时是完全正常的。于是第 3 项还在正常跑，第 4、5 项就被判失败并发告警。
    ///
    /// 现在起算点是「队列上一次推进」，且有项在跑时一律不判。
    /// </summary>
    [Fact]
    public async Task 有项在跑时排队中的项不因整次执行超时而被判失败()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 5, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);

        await RunPassAsync();
        Assert.Equal(ExecutionItemStatus.Running, (await ItemStatusesAsync(runId))[0]);

        // 整次执行开始于 10 小时前（超过 2×240 分钟的阈值），
        // 但第一项是刚刚才开始跑的——队列一直在推进，只是每一项都很慢。
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            await db.ExecutionRuns.Where(r => r.Id == runId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.StartedAt, now.AddHours(-10))
                    .SetProperty(r => r.CreatedAt, now.AddHours(-10)));

            // 在跑的那一项本身没有超时（单项超时 240 分钟）
            await db.ExecutionRunItems.Where(i => i.RunId == runId && i.SortOrder == 0)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.StartedAt, now.AddMinutes(-10)));
        }

        await RunPassAsync();

        var statuses = await ItemStatusesAsync(runId);
        Assert.Equal(ExecutionItemStatus.Running, statuses[0]);
        Assert.All(statuses.Skip(1), s => Assert.Equal(ExecutionItemStatus.Pending, s));
        Assert.DoesNotContain(ExecutionItemStatus.Failed, statuses);
    }

    // ---------- 用例六：队列名单 ----------

    /// <summary>
    /// 「什么时候轮到我」这个问题，数字（queue-status 的几个计数）回答不了。
    /// 并发度 1 的计划挂着十台服务器时，人要看的是名单和它的顺序——
    /// 名单的顺序必须与执行器放行的顺序一致，否则「还要等几个」就读不出来。
    /// </summary>
    [Fact]
    public async Task 队列名单按放行顺序列出在跑的和在等的项()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 3, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);

        await RunPassAsync();

        await using var scope = _services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueueService>();
        var snapshot = await queue.GetQueueAsync();

        // 快照是全局的（同一个库里可能还有别的执行），只看这一次的那几项
        var mine = snapshot.Items.Where(i => i.RunId == runId).ToList();
        Assert.Equal(3, mine.Count);
        Assert.Equal([0, 1, 2], mine.Select(i => i.SortOrder));

        Assert.Equal("running", mine[0].Status);
        Assert.Equal(ExecutionQueueWaits.Running, mine[0].Wait);
        Assert.All(mine.Skip(1), i =>
        {
            Assert.Equal("pending", i.Status);
            // 全局上传名额一个都没占（预检项不占），这两项等的是前一项跑完
            Assert.Equal(ExecutionQueueWaits.WaitingInRun, i.Wait);
        });

        // 名单要能指认「是谁在等」：只有 GUID 的话这张表跟数字没有区别
        Assert.All(mine, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.TaskName));
            Assert.False(string.IsNullOrWhiteSpace(i.ClientName));
            Assert.Equal("precheck_task", i.CommandType);
            Assert.Equal(1, i.RunMaxConcurrent);
        });

        // 跑完的项不留在队列里：它属于运行记录，混进来会把「还要等几个」冲淡
        await CompleteItemAsync(runId, 0, (await TaskIdsOfAsync(runId))[0]);
        await RunPassAsync();

        var after = (await queue.GetQueueAsync()).Items.Where(i => i.RunId == runId).ToList();
        Assert.Equal(2, after.Count);
        Assert.Equal(1, after[0].SortOrder);
        Assert.Equal(ExecutionQueueWaits.Running, after[0].Wait);
    }

    /// <summary>
    /// 「等名额」和「等前一项」不是一回事，混成一个数就会写出
    /// 「0 个正在传 / 2 个排队中 · 有空余名额」这种自相矛盾的话。
    /// 判据只有一份（ExecutionQueueWaits），这里直接钉住它。
    /// </summary>
    [Fact]
    public void 等名额与等前一项分得开()
    {
        // 本轮轮不到它：先说「等前一项」，与名额满不满无关
        Assert.Equal(ExecutionQueueWaits.WaitingInRun,
            ExecutionQueueWaits.Classify(1, 1, CommandType.UploadCandidate, uploadSlotFull: true));

        // 轮得到它，但它要建上传会话而名额满了
        Assert.Equal(ExecutionQueueWaits.WaitingForUploadSlot,
            ExecutionQueueWaits.Classify(0, 1, CommandType.UploadCandidate, uploadSlotFull: true));

        // 预检不占名额：名额满了也照样放行，挡住它等于让计划连扫描都不许开始
        Assert.Equal(ExecutionQueueWaits.WaitingInRun,
            ExecutionQueueWaits.Classify(0, 1, CommandType.PrecheckTask, uploadSlotFull: true));
        Assert.False(ExecutionQueueWaits.ConsumesUploadSlot(CommandType.PrecheckTask));
    }

    // ---------- 用例七：预检自己报了结论就不再空等宽限期 ----------

    /// <summary>
    /// 预检说「这次没有新备份」时，这一项当场就该结案。
    ///
    /// 原先唯一的判据是「等满 no_upload_grace（默认 10 分钟）还没有上传会话就判成功」，
    /// 于是严格顺序的计划里，每一个没有新备份的任务都要在「执行中」上白挂十分钟，
    /// 后面每一项跟着顺延——三台服务器的计划能空转半小时，而界面上什么都不显示。
    /// </summary>
    [Fact]
    public async Task 预检报没有新备份时当场结案而不是等满宽限期()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 2, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);
        await RunPassAsync();

        await SucceedPrecheckAsync(runId, 0, (EnumMapping.ToSnakeCase(PrecheckStatus.NoNewBackup), null));
        await RunPassAsync();

        var statuses = await ItemStatusesAsync(runId);
        Assert.Equal(ExecutionItemStatus.Succeeded, statuses[0]);
        // 结案了，后面那一项立刻被放行——这才是「严格顺序」该有的节奏
        Assert.Equal(ExecutionItemStatus.Running, statuses[1]);
        Assert.Equal("这次没有新备份", await ItemMessageAsync(runId, 0));
    }

    /// <summary>
    /// 预检没通过时这一项必须判失败。
    ///
    /// 原先它同样等满宽限期，然后写「没有需要上传的新备份」并判成功——而告警中心那边
    /// 正为同一件事报着 size_abnormal。两份记录对同一件事给出相反的结论时，
    /// 人会相信执行记录，因为它更具体。这是备份监控系统能给出的最坏的一种答案。
    /// </summary>
    [Fact]
    public async Task 预检没通过时这一项判失败而不是报成功()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 1, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);
        await RunPassAsync();

        await SucceedPrecheckAsync(runId, 0, (EnumMapping.ToSnakeCase(PrecheckStatus.SizeAbnormal), null));
        await RunPassAsync();

        Assert.Equal(ExecutionItemStatus.Failed, (await ItemStatusesAsync(runId))[0]);
        Assert.Contains("备份大小超出设定范围", await ItemMessageAsync(runId, 0));
    }

    /// <summary>
    /// 扫出了新备份、但没有下发任何上传（任务不是自动模式，或这一份刚被人取消过上传）：
    /// 等下去不会有会话，而「没有需要上传的新备份」是假话——它会让人以为今天不用管。
    /// </summary>
    [Fact]
    public async Task 扫出新备份却没下发上传时说清楚是哪一种()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 1, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);
        await RunPassAsync();

        await SucceedPrecheckAsync(runId, 0,
            (PrecheckResultPayload.StatePassed, PrecheckResultPayload.StateNotDispatched));
        await RunPassAsync();

        Assert.Equal(ExecutionItemStatus.Succeeded, (await ItemStatusesAsync(runId))[0]);
        var message = await ItemMessageAsync(runId, 0);
        Assert.Contains("没有自动下发上传", message);
        Assert.DoesNotContain("没有需要上传的新备份", message);
    }

    /// <summary>上传已经下发出去了就继续等真会话，不能因为「此刻还没有会话」就下结论。</summary>
    [Fact]
    public async Task 上传已下发时继续等会话而不是当场结案()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 1, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);
        await RunPassAsync();

        await SucceedPrecheckAsync(runId, 0,
            (PrecheckResultPayload.StatePassed, PrecheckResultPayload.StateDispatched));
        await RunPassAsync();

        Assert.Equal(ExecutionItemStatus.Running, (await ItemStatusesAsync(runId))[0]);
    }

    /// <summary>
    /// 会话建于这一项开跑**之前**时也必须找得到它。
    ///
    /// Agent 建会话的幂等键是候选键，同一份备份先前已经传完入库时，服务端把那个
    /// committed 的老会话原样交回、不新建行。定位会话只靠「本项开跑之后新建的」
    /// 那一版于是查出 0 条：上传几秒钟就结束了，这一项却一直挂到单项超时
    /// （现场那次是计划的 60 分钟）才被判失败，严格顺序时后面每一项跟着顺延一小时。
    /// 清单里逐单元写着候选 ID，那才是这一项真正涉及的那几份备份。
    /// </summary>
    [Fact]
    public async Task 复用先前已入库的会话时这一项照样当场结案()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 2, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);
        await RunPassAsync();

        var candidateIds = await SucceedPrecheckAsync(runId, 0,
            (PrecheckResultPayload.StatePassed, PrecheckResultPayload.StateQueued));

        // 会话早于这一项开跑：现场那一次是 10:55 传完入库、14:03 计划再跑到同一份备份
        await SeedCommittedSessionAsync(runId, 0, candidateIds[0], DateTime.UtcNow.AddHours(-3));

        await RunPassAsync();

        var statuses = await ItemStatusesAsync(runId);
        Assert.Equal(ExecutionItemStatus.Succeeded, statuses[0]);
        Assert.Equal("备份已入库", await ItemMessageAsync(runId, 0));
        // 结案了，后面那一项立刻被放行——这正是原先被白白顺延掉的那一小时
        Assert.Equal(ExecutionItemStatus.Running, statuses[1]);
    }

    /// <summary>
    /// 排队的上传已经跑完、却一个会话都没留下时，要给结论而不是无限等下去。
    ///
    /// queued 的单元没有上传指令 ID（排进队列时指令还没建出来），
    /// 「上传指令是不是已经死了」那段检查够不着它们，于是这一项永远等下去。
    /// </summary>
    [Fact]
    public async Task 排队的上传跑完却没留下会话时不再无限等下去()
    {
        var (planId, _) = await SeedPlanAsync(taskCount: 1, maxConcurrent: 1);
        var runId = await TriggerAsync(planId);
        await RunPassAsync();

        await SucceedPrecheckAsync(runId, 0,
            (PrecheckResultPayload.StatePassed, PrecheckResultPayload.StateQueued));
        await RunPassAsync();

        Assert.Equal(ExecutionItemStatus.Failed, (await ItemStatusesAsync(runId))[0]);
        Assert.Contains("没有留下任何上传会话", await ItemMessageAsync(runId, 0));
    }

    // ---------- 基础设施 ----------

    /// <summary>
    /// 把某一项的预检指令置成功，并按给定的每单元结论写出 result_payload；
    /// 返回为这些单元建出来的候选 ID。
    ///
    /// 候选写成真行而不是随手 Guid.NewGuid()：上传会话对它有外键，
    /// 而「按清单里的候选 ID 去找会话」这条路正是要被验的那一条。
    /// </summary>
    private async Task<List<Guid>> SucceedPrecheckAsync(
        Guid runId, int sortOrder, params (string Status, string? UploadState)[] entries)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var item = await db.ExecutionRunItems.AsNoTracking()
            .SingleAsync(i => i.RunId == runId && i.SortOrder == sortOrder);
        Assert.NotNull(item.CommandId);

        var now = DateTime.UtcNow;
        var candidateIds = new List<Guid>();
        string? payload = null;
        var index = 0;
        foreach (var (status, uploadState) in entries)
        {
            var candidate = new CandidateBackupSet
            {
                Id = Guid.NewGuid(),
                ClientId = item.ClientId,
                TaskId = item.TaskId,
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
            candidateIds.Add(candidate.Id);

            payload = PrecheckResultPayload.Upsert(payload, new PrecheckResultPayload.Entry(
                $"unit-{index++}", candidate.Id, status, null, uploadState));
        }
        await db.SaveChangesAsync();

        await db.Commands.Where(c => c.Id == item.CommandId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CommandStatus.Succeeded)
                .SetProperty(c => c.CompletedAt, now)
                .SetProperty(c => c.ResultPayload, payload));

        return candidateIds;
    }

    /// <summary>给某个候选补一个已入库的上传会话，created_at 可以早于这一项开跑的时刻。</summary>
    private async Task SeedCommittedSessionAsync(Guid runId, int sortOrder, Guid candidateId, DateTime createdAt)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var item = await db.ExecutionRunItems.AsNoTracking()
            .SingleAsync(i => i.RunId == runId && i.SortOrder == sortOrder);

        db.UploadSessions.Add(new Core.Entities.Upload.UploadSession
        {
            Id = Guid.NewGuid(),
            ClientId = item.ClientId,
            TaskId = item.TaskId,
            CandidateBackupSetId = candidateId,
            Status = UploadStatus.Committed,
            TotalFiles = 1,
            TotalBytes = 1,
            StartedAt = createdAt,
            LastActivityAt = createdAt,
            CompletedAt = createdAt,
            CommittedAt = createdAt,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> ItemMessageAsync(Guid runId, int sortOrder)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ExecutionRunItems.AsNoTracking()
            .Where(i => i.RunId == runId && i.SortOrder == sortOrder)
            .Select(i => i.Message)
            .SingleAsync();
    }


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

    private async Task<List<Guid>> TaskIdsOfAsync(Guid runId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ExecutionRunItems.AsNoTracking()
            .Where(i => i.RunId == runId)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.TaskId)
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
