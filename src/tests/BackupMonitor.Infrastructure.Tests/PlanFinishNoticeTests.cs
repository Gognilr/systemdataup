using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Execution;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 备份计划完成回执。
///
/// 三块：
/// 1. 这次执行要不要发（计划驱动、到点触发、计划开了回执，三者缺一不可）
/// 2. 回执正文长什么样（结论四行 + 失败项，截断与兜底）
/// 3. 这个任务已经被计划覆盖了没有（覆盖了就不再单独发任务回执，以计划为准）
///
/// 第 1、3 块错了**不会报错**，只会静默地多发或少发一条，而「群里今天怎么多了 / 少了一条」
/// 这种事没人会去追——这是这一组存在的主要理由。
/// 第 2 块错了看得见，但它决定的是收件人在手机上那一眼能不能看懂：
/// 失败原因里塞进整段堆栈、或者 50 条失败全列出来，都会让这条消息变成没人读的东西。
/// </summary>
[Collection("postgres")]
public class PlanFinishNoticeTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public PlanFinishNoticeTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ── 判断一：这次执行要不要发 ─────────────────────────────────────

    /// <summary>计划到点跑完、且计划开了回执——这是唯一该发的组合。</summary>
    [Fact]
    public void 到点触发且计划开了回执时要发()
    {
        var run = NewRun(triggerSource: "schedule", planId: Guid.NewGuid());

        Assert.True(SequentialExecutionWorker.ShouldNotifyPlanFinish(
            run, new BackupPlan { NotifyOnFinish = true }));
    }

    /// <summary>
    /// 全部成功也要发。
    ///
    /// 这一条单独钉：只在出事时发的摘要会退化成另一种告警，而这条回执真正不可替代的
    /// 价值恰恰是那句「12 项全成功」——收到它，才知道计划昨晚确实跑了。
    /// </summary>
    [Fact]
    public void 全部成功也要发()
    {
        var run = NewRun(triggerSource: "schedule", planId: Guid.NewGuid());
        run.Status = BatchStatus.Completed;
        run.TotalItems = 12;
        run.SucceededItems = 12;
        run.FailedItems = 0;

        Assert.True(SequentialExecutionWorker.ShouldNotifyPlanFinish(
            run, new BackupPlan { NotifyOnFinish = true }));
    }

    /// <summary>人手点的「立即执行」不发：点的人正盯着页面看，再推一条到群里是纯噪音。</summary>
    [Fact]
    public void 手动触发的执行不发()
    {
        var run = NewRun(triggerSource: "manual", planId: Guid.NewGuid());

        Assert.False(SequentialExecutionWorker.ShouldNotifyPlanFinish(
            run, new BackupPlan { NotifyOnFinish = true }));
    }

    /// <summary>计划没开这个开关就不发——开关得真的管用。</summary>
    [Fact]
    public void 计划没开回执就不发()
    {
        var run = NewRun(triggerSource: "schedule", planId: Guid.NewGuid());

        Assert.False(SequentialExecutionWorker.ShouldNotifyPlanFinish(
            run, new BackupPlan { NotifyOnFinish = false }));
    }

    /// <summary>没有计划的执行（批量上传也走同一个队列）不发：它根本不是一次计划。</summary>
    [Fact]
    public void 不是计划驱动的执行不发()
    {
        var run = NewRun(triggerSource: "schedule", planId: null);

        Assert.False(SequentialExecutionWorker.ShouldNotifyPlanFinish(run, plan: null));
    }

    // ── 回执正文 ─────────────────────────────────────────────────────

    /// <summary>
    /// 结论里必须带上「入库几份、共多大」。
    ///
    /// 「跑完了」和「真的备进东西了」是两回事：一个全部成功但 0 份入库的计划，
    /// 只看项数那一行完全看不出异常，而那恰恰是最该被看见的一种异常。
    /// </summary>
    [Fact]
    public void 结论里带上入库份数和总大小()
    {
        var run = NewRun("schedule", Guid.NewGuid());
        run.Status = BatchStatus.Completed;
        run.TotalItems = 2;
        run.SucceededItems = 2;

        var body = SequentialExecutionWorker.BuildPlanFinishBody("恒源服务器数据备份", run,
        [
            Ok("恒源_OA服务器", "OA 数据库全量", 1, 3L * 1024 * 1024 * 1024),
            Ok("恒源_U8服务器", "U8 账套备份", 18, 21L * 1024 * 1024 * 1024)
        ]);

        Assert.Contains("计划: 恒源服务器数据备份", body);
        Assert.Contains("结果: 全部成功", body);
        Assert.Contains("共 2 项，成功 2，失败 0", body);
        Assert.Contains("入库 19 份，共 24 GB", body);
    }

    /// <summary>
    /// 明细一行一个任务，不是一行一个备份集。
    ///
    /// U8 那种一个任务底下十几个账套，按备份集列就是十几行——那正是任务级回执
    /// 被关掉的原因（18 个账套发 18 条）。这里必须压成一行。
    /// </summary>
    [Fact]
    public void 明细一行一个任务并带上份数和大小()
    {
        var run = NewRun("schedule", Guid.NewGuid());
        run.Status = BatchStatus.Completed;

        var body = SequentialExecutionWorker.BuildPlanFinishBody("计划", run,
            [Ok("瑞来_U8服务器", "U8 账套备份", 18, 21L * 1024 * 1024 * 1024)]);

        Assert.Contains("明细：", body);
        Assert.Contains("✓ 瑞来_U8服务器 / U8 账套备份 — 18 份，21 GB", body);
        Assert.Single(body.Split(Environment.NewLine).Where(l => l.Contains("U8 账套备份")));
    }

    /// <summary>
    /// 成功但一份都没进，要显示成「无新备份」。
    ///
    /// 预检通过、源目录没有新文件，也算成功。一周才备一两次的现场这种情况很常见，
    /// 而它和「真的备进去了」显示成一样的话，「这周到底备没备」就看不出来了——
    /// 这一行可能比其他所有行都重要。
    /// </summary>
    [Fact]
    public void 成功但没有新备份要单独说明()
    {
        var run = NewRun("schedule", Guid.NewGuid());
        run.Status = BatchStatus.Completed;

        var body = SequentialExecutionWorker.BuildPlanFinishBody("计划", run,
            [Ok("机器", "任务", 0, 0)]);

        Assert.Contains("✓ 机器 / 任务 — 无新备份", body);
        Assert.DoesNotContain("0 份，0 B", body);
    }

    /// <summary>耗时要写出来：这周 13 分钟、下周 2 小时，说明有东西不对劲。</summary>
    [Fact]
    public void 结束时间带上耗时()
    {
        var run = NewRun("schedule", Guid.NewGuid());
        run.StartedAt = new DateTime(2026, 9, 12, 0, 0, 4, DateTimeKind.Utc);
        run.FinishedAt = run.StartedAt.Value.AddMinutes(13).AddSeconds(10);

        var body = SequentialExecutionWorker.BuildPlanFinishBody("计划", run, []);

        Assert.Contains("（耗时 13 分 10 秒）", body);
    }

    /// <summary>失败项要带上是哪台机器的哪个任务，以及为什么——这是唯一需要人动手的部分。</summary>
    [Fact]
    public void 失败项列出机器任务和原因()
    {
        var run = NewRun("schedule", Guid.NewGuid());
        run.Status = BatchStatus.Partial;
        run.TotalItems = 3;
        run.SucceededItems = 1;
        run.FailedItems = 2;

        var body = SequentialExecutionWorker.BuildPlanFinishBody("每日凌晨备份", run,
        [
            new("恒源_OA服务器", "OA 数据库全量", ExecutionItemStatus.Timeout, null, 0, 0),
            new("瑞来_U8服务器", "U8 附件库", ExecutionItemStatus.Failed, "预检未通过：源目录 3 天没有新文件", 0, 0)
        ]);

        Assert.Contains("结果: 部分成功", body);
        // 超时要和「跑了但失败了」分开说：前者多半是机器关着或网断了，
        // 后者才需要去看备份本身——处置完全不同。
        Assert.Contains("✗ 恒源_OA服务器 / OA 数据库全量 — 超时", body);
        Assert.Contains("✗ 瑞来_U8服务器 / U8 附件库 — 预检未通过：源目录 3 天没有新文件", body);
    }

    /// <summary>没有原因文本的失败不能只留一个破折号,兜个「失败」。</summary>
    [Fact]
    public void 失败原因为空时兜一个失败()
    {
        var run = NewRun("schedule", Guid.NewGuid());
        run.Status = BatchStatus.Failed;

        var body = SequentialExecutionWorker.BuildPlanFinishBody("计划", run,
            [new("机器", "任务", ExecutionItemStatus.Failed, "   ", 0, 0)]);

        Assert.Contains("✗ 机器 / 任务 — 失败", body);
    }

    /// <summary>
    /// 长原因要截断,而且不能把换行原样带进去。
    ///
    /// 失败原因里塞进整段堆栈是常事,原样发到钉钉群会把一条消息撑成一屏。
    /// </summary>
    [Fact]
    public void 长原因截断且不带换行()
    {
        var run = NewRun("schedule", Guid.NewGuid());
        var message = "第一行" + Environment.NewLine + new string('长', 200);

        var body = SequentialExecutionWorker.BuildPlanFinishBody("计划", run,
            [new("机器", "任务", ExecutionItemStatus.Failed, message, 0, 0)]);

        var line = body.Split(Environment.NewLine).Single(l => l.Contains("✗ 机器"));
        Assert.EndsWith("…", line);
        Assert.True(line.Length < 100, $"失败行太长了：{line.Length} 字");
        Assert.DoesNotContain(new string('长', 200), body);
    }

    /// <summary>
    /// 明细太多时只列前 20 条,并说明还有多少。
    ///
    /// 计划里通常只有几项,这个上限是防御性的。关键数字(共几项、成功几项、入库几份)
    /// 在上面已经给全了,列表截断不影响结论。
    /// </summary>
    [Fact]
    public void 明细超过二十条只列前二十()
    {
        var run = NewRun("schedule", Guid.NewGuid());
        run.Status = BatchStatus.Failed;
        run.TotalItems = 25;
        run.FailedItems = 25;

        var items = Enumerable.Range(1, 25)
            .Select(i => new SequentialExecutionWorker.PlanFinishItem(
                "机器", $"任务{i}", ExecutionItemStatus.Failed, "失败", 0, 0))
            .ToList();

        var body = SequentialExecutionWorker.BuildPlanFinishBody("计划", run, items);

        Assert.Equal(20, body.Split(Environment.NewLine).Count(l => l.TrimStart().StartsWith('✗')));
        Assert.Contains("另有 5 项，详见管理页面", body);
        // 总数仍然要说全：列表截断了，结论不能跟着截断
        Assert.Contains("失败 25", body);
    }

    /// <summary>标题要带计划名——群里同时有几个计划时,光看「备份计划完成」分不出是哪个。</summary>
    [Fact]
    public void 标题带上计划名()
    {
        Assert.Equal("备份计划完成：每日凌晨备份",
            SequentialExecutionWorker.BuildPlanFinishSubject("每日凌晨备份"));
    }

    private static SequentialExecutionWorker.PlanFinishItem Ok(
        string client, string task, int sets, long bytes) =>
        new(client, task, ExecutionItemStatus.Succeeded, null, sets, bytes);

    // ── 判断二：任务是否已被计划覆盖 ──────────────────────────────────

    /// <summary>被一个开了回执的启用计划管着 → 任务自己那条不再发，以计划为准。</summary>
    [Fact]
    public async Task 任务在开了回执的计划里就算被覆盖()
    {
        var taskId = await SeedTaskInPlanAsync(planEnabled: true, planNotifies: true);

        Assert.True(await IsCoveredAsync(taskId));
    }

    /// <summary>
    /// 计划没开回执 → **不算覆盖**，任务自己那条照发。
    ///
    /// 这一条是整组里最容易写反的：只按「在不在计划里」判的话，人在任务上勾了回执
    /// 却什么都收不到，而界面上没有任何地方解释为什么。
    /// </summary>
    [Fact]
    public async Task 计划没开回执时不算覆盖()
    {
        var taskId = await SeedTaskInPlanAsync(planEnabled: true, planNotifies: false);

        Assert.False(await IsCoveredAsync(taskId));
    }

    /// <summary>计划停用了就不会跑，自然也不会有汇总——这时任务自己那条必须还在。</summary>
    [Fact]
    public async Task 计划已停用时不算覆盖()
    {
        var taskId = await SeedTaskInPlanAsync(planEnabled: false, planNotifies: true);

        Assert.False(await IsCoveredAsync(taskId));
    }

    /// <summary>压根不在任何计划里的任务，按它自己勾的来。</summary>
    [Fact]
    public async Task 不在任何计划里的任务不算被覆盖()
    {
        var taskId = await SeedTaskInPlanAsync(planEnabled: true, planNotifies: true, attachToPlan: false);

        Assert.False(await IsCoveredAsync(taskId));
    }

    private static ExecutionRun NewRun(string triggerSource, Guid? planId) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = planId,
        TriggerSource = triggerSource,
        Status = BatchStatus.Completed
    };

    private async Task<bool> IsCoveredAsync(Guid taskId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await UploadCommitWorker.IsCoveredByNotifyingPlanAsync(db, taskId, CancellationToken.None);
    }

    private async Task<Guid> SeedTaskInPlanAsync(
        bool planEnabled, bool planNotifies, bool attachToPlan = true)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"plannotice-{suffix}",
            Hostname = $"plan-{suffix[..8]}",
            DisplayName = $"计划回执测试-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);

        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"计划回执任务-{suffix[..8]}",
            ApplicationName = "测试",
            SourcePath = @"C:\\backup",
            NotifyOnSuccess = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.BackupTasks.Add(task);

        var plan = new BackupPlan
        {
            Id = Guid.NewGuid(),
            Name = $"计划回执-{suffix[..8]}",
            Enabled = planEnabled,
            NotifyOnFinish = planNotifies,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.BackupPlans.Add(plan);

        if (attachToPlan)
        {
            db.BackupPlanItems.Add(new BackupPlanItem
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                TaskId = task.Id,
                SortOrder = 1,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync();
        return task.Id;
    }
}
