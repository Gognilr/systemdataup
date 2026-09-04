using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 备份集按「客户端 + 任务」归拢。
///
/// U8 一个任务 18 个账套，平铺列表一天就是 18 行，一周 126 行，中间还夹着别的任务——
/// 「这个任务今天备齐了没有」得靠人一行行数，而这正是这张表存在的唯一理由。
/// 归拢之后每一组必须自带结论：几个账套、最新一份什么时候、有没有不可用的。
/// </summary>
[Collection("postgres")]
public class BackupSetGroupingTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public BackupSetGroupingTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IUploadStorage, UploadStorage>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddKeyedSingleton(QueueKeys.Verify, (_, _) => Channel.CreateUnbounded<WorkItem>());
        sc.AddScoped<IBackupSetService, BackupSetService>();
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task 十八个账套归成一行并数出账套个数()
    {
        var taskId = await SeedTaskAsync(Enumerable.Range(1, 18).Select(i => $"ZT{i:000}").ToArray());

        var group = await GroupOfAsync(taskId);

        Assert.Equal(18, group.BackupSetCount);
        Assert.Equal(18, group.BusinessUnitCount);
        Assert.Equal(18, group.AvailableCount);
        Assert.Equal(0, group.NotAvailableCount);
    }

    [Fact]
    public async Task 归拢行报出不可用的份数()
    {
        // 折叠之后这个数是唯一的警报：18 个账套里 2 个校验失败，
        // 在别的任何一列上都表现为「这个任务有备份」。
        var taskId = await SeedTaskAsync(
            ["ZT001", "ZT002", "ZT003"],
            statuses: [BackupSetStatus.Available, BackupSetStatus.VerificationFailed, BackupSetStatus.Quarantined]);

        var group = await GroupOfAsync(taskId);

        Assert.Equal(3, group.BackupSetCount);
        Assert.Equal(1, group.AvailableCount);
        Assert.Equal(2, group.NotAvailableCount);
    }

    [Fact]
    public async Task 单单元任务的账套数算作一个()
    {
        // 没有业务单元的任务（单份备份、老数据）COUNT(DISTINCT null) 是 0，
        // 界面上显示「0 个业务单元」会让人以为这个任务什么都没备。
        var taskId = await SeedTaskAsync([null]);

        var group = await GroupOfAsync(taskId);

        Assert.Equal(1, group.BackupSetCount);
        Assert.Equal(1, group.BusinessUnitCount);
    }

    [Fact]
    public async Task 最新业务时间取这一组里最晚的那一份()
    {
        var latest = DateTime.UtcNow.AddMinutes(-1);
        var taskId = await SeedTaskAsync(
            ["ZT001", "ZT002"],
            businessTimes: [latest.AddDays(-3), latest]);

        var group = await GroupOfAsync(taskId);

        Assert.NotNull(group.LatestBusinessTime);
        Assert.True(Math.Abs((group.LatestBusinessTime!.Value - latest).TotalSeconds) < 1,
            "归拢行要报最新那一份的业务时间，否则「这个任务还在不在出备份」就读不出来");
    }

    [Fact]
    public async Task 归拢与展开用的是同一套筛选口径()
    {
        // 归拢行说 3 份、展开只列出 2 份，是没有任何办法从界面上分辨
        // 「筛选口径不一样」还是「真的少了一份备份」的。
        var taskId = await SeedTaskAsync(
            ["ZT001", "ZT002", "ZT003"],
            statuses: [BackupSetStatus.Available, BackupSetStatus.Available, BackupSetStatus.Quarantined]);

        var query = new BackupQuery { TaskId = taskId, Status = "available", Page = 1, PageSize = 50 };
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupSetService>();

        var group = Assert.Single((await svc.GetGroupsAsync(query)).Items);
        var detail = await svc.GetListAsync(query);

        Assert.Equal(2, group.BackupSetCount);
        Assert.Equal(group.BackupSetCount, detail.Items.Count);
    }

    // ---------- 基础设施 ----------

    private async Task<BackupSetGroupDto> GroupOfAsync(Guid taskId)
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IBackupSetService>()
            .GetGroupsAsync(new BackupQuery { TaskId = taskId, Page = 1, PageSize = 50 });
        return Assert.Single(result.Items);
    }

    /// <summary>建一个任务，每个 unitNames 元素造一份备份集（null 表示这一份不挂业务单元）。</summary>
    private async Task<Guid> SeedTaskAsync(
        string?[] unitNames,
        BackupSetStatus[]? statuses = null,
        DateTime[]? businessTimes = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"grp-{suffix}",
            Hostname = $"grp-host-{suffix[..8]}",
            DisplayName = $"归拢测试-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"grp-task-{suffix[..8]}",
            ApplicationName = "GroupingTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        db.BackupTasks.Add(task);

        for (var i = 0; i < unitNames.Length; i++)
        {
            var unitName = unitNames[i];
            BusinessUnit? unit = null;
            if (unitName is not null)
            {
                unit = new BusinessUnit
                {
                    Id = Guid.NewGuid(),
                    TaskId = task.Id,
                    ExternalKey = $"{unitName}-{suffix[..8]}",
                    DisplayName = unitName,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.BusinessUnits.Add(unit);
            }

            var candidate = new CandidateBackupSet
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                TaskId = task.Id,
                BusinessUnitId = unit?.Id,
                CandidateKey = $"grp-{suffix}-{i}",
                SourceRoot = @"D:\data\2026",
                DiscoveredAt = now,
                PrecheckStatus = PrecheckStatus.Passed,
                TotalFiles = 1,
                TotalBytes = 12,
                CreatedAt = now,
                UpdatedAt = now
            };
            var session = new Core.Entities.Upload.UploadSession
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                TaskId = task.Id,
                CandidateBackupSetId = candidate.Id,
                Status = UploadStatus.Committed,
                TotalFiles = 1,
                TotalBytes = 12,
                StartedAt = now,
                LastActivityAt = now,
                CompletedAt = now,
                CommittedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.CandidateBackupSets.Add(candidate);
            db.UploadSessions.Add(session);
            await db.SaveChangesAsync();

            db.BackupSets.Add(new BackupSet
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                TaskId = task.Id,
                BusinessUnitId = unit?.Id,
                SourceCandidateId = candidate.Id,
                UploadSessionId = session.Id,
                BackupSetCode = $"BS-GRP-{suffix[..8]}-{i:000}",
                Status = statuses is not null ? statuses[i] : BackupSetStatus.Available,
                DiscoveredAt = now,
                BackupBusinessTime = businessTimes is not null ? businessTimes[i] : now.AddMinutes(-i),
                UploadedAt = now.AddMinutes(-i),
                VerifiedAt = now,
                TotalFiles = 1,
                TotalBytes = 12,
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        return task.Id;
    }
}
