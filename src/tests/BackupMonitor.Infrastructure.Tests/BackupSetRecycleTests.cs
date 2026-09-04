using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Retention;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 备份集删除（待办方案 D）：删除走回收站，误删能捞回来；
/// 「立即彻底删除」只对回收站开放，且守卫与保留清理完全一致——
/// 自动清理不敢删的东西，人在界面上点一下也不该能删掉。
/// </summary>
[Collection("postgres")]
public class BackupSetRecycleTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;
    private string _repositoryRoot = null!;

    public BackupSetRecycleTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "bm-recycle-test", Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(_repositoryRoot);

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
        // 重校验队列在本测试里不会被用到，但构造函数需要它
        sc.AddKeyedSingleton(QueueKeys.Verify, (_, _) => Channel.CreateUnbounded<WorkItem>());
        sc.AddScoped<IBackupSetService, BackupSetService>();
        _services = sc.BuildServiceProvider();

        await SetRepositoryRootSettingAsync();
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task 删除进回收站后可以还原()
    {
        var (setId, _) = await SeedAsync();

        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IBackupSetService>().RecycleAsync(setId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var set = await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setId);
            Assert.Equal(BackupSetStatus.RecycleBin, set.Status);
            Assert.NotNull(set.RetentionUntil);   // 到期后由保留清理物理删除
        }

        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IBackupSetService>().RestoreFromRecycleBinAsync(setId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var set = await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setId);
            Assert.Equal(BackupSetStatus.Available, set.Status);
            Assert.Null(set.RetentionUntil);
        }
    }

    [Fact]
    public async Task 已锁定的备份集删不掉并给出原因()
    {
        var (setId, _) = await SeedAsync(locked: true);

        await using var scope = _services.CreateAsyncScope();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => scope.ServiceProvider.GetRequiredService<IBackupSetService>().RecycleAsync(setId));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("锁定", ex.Message);
    }

    [Fact]
    public async Task 正在恢复下载的备份集删不掉()
    {
        var (setId, clientId) = await SeedAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RestoreRequests.Add(new Core.Entities.Restore.RestoreRequest
            {
                Id = Guid.NewGuid(),
                BackupSetId = setId,
                Status = RestoreRequestStatus.Downloading,
                Purpose = "误删守卫测试",
                // restore_requests.requested_by 是指向 users 的外键，用 V005 的种子管理员
                RequestedBy = Guid.Parse("c0000001-0000-0000-0000-000000000001"),
                RequestedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var scope2 = _services.CreateAsyncScope();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => scope2.ServiceProvider.GetRequiredService<IBackupSetService>().RecycleAsync(setId));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task 彻底删除只对回收站开放且真的删掉目录()
    {
        var dir = MakeRepositoryDirectory();
        var (setId, _) = await SeedAsync(repositoryPath: dir);

        // 可用状态直接彻底删除 → 拒绝。不可撤销的操作不该有一键直达的路径。
        await using (var scope = _services.CreateAsyncScope())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => scope.ServiceProvider.GetRequiredService<IBackupSetService>().PurgeAsync(setId));
            Assert.Equal(409, ex.StatusCode);
        }
        Assert.True(Directory.Exists(dir), "被拒绝的删除不能碰磁盘");

        await using (var scope = _services.CreateAsyncScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IBackupSetService>();
            await svc.RecycleAsync(setId);
            await svc.PurgeAsync(setId);
        }

        Assert.False(Directory.Exists(dir), "彻底删除要真的删掉仓库目录");

        await using var scope2 = _services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(BackupSetStatus.Deleted,
            (await db2.BackupSets.AsNoTracking().SingleAsync(s => s.Id == setId)).Status);
    }

    [Fact]
    public async Task 批量删除逐条成败互不牵连()
    {
        var (okA, _) = await SeedAsync();
        var (lockedId, _) = await SeedAsync(locked: true);
        var (okB, _) = await SeedAsync();

        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupSetService>();

        var result = await svc.RecycleBatchAsync(new BackupSetBatchRequest
        {
            BackupSetIds = new List<Guid> { okA, lockedId, okB }
        });

        // 中间那一份被锁定，本来就该被拒——但它不该把另外两份一起拖住。
        Assert.Equal(2, result.SuccessCount);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(lockedId, failure.BackupSetId);
        Assert.Contains("锁定", failure.Message);
        Assert.False(string.IsNullOrEmpty(failure.BackupSetCode), "失败明细里要能对上是哪一份");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var states = await db.BackupSets.AsNoTracking()
            .Where(s => s.Id == okA || s.Id == lockedId || s.Id == okB)
            .ToDictionaryAsync(s => s.Id, s => s.Status);
        Assert.Equal(BackupSetStatus.RecycleBin, states[okA]);
        Assert.Equal(BackupSetStatus.RecycleBin, states[okB]);
        Assert.Equal(BackupSetStatus.Available, states[lockedId]);
    }

    [Fact]
    public async Task 批量超过单批上限直接拒绝()
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupSetService>();

        var tooMany = Enumerable.Range(0, BackupSetService.MaxBatchSize + 1)
            .Select(_ => Guid.NewGuid()).ToList();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => svc.PurgeBatchAsync(new BackupSetBatchRequest { BackupSetIds = tooMany }));
        Assert.Equal(400, ex.StatusCode);

        // 空选也该被挡住：批量端点不该在一个空列表上「成功」
        var empty = await Assert.ThrowsAsync<BusinessException>(
            () => svc.RecycleBatchAsync(new BackupSetBatchRequest()));
        Assert.Equal(400, empty.StatusCode);
    }

    [Fact]
    public async Task 默认列表不含已删除但显式筛得到()
    {
        var (setId, clientId) = await SeedAsync(repositoryPath: MakeRepositoryDirectory());

        await using (var scope = _services.CreateAsyncScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IBackupSetService>();
            await svc.RecycleAsync(setId);
            await svc.PurgeAsync(setId);
        }

        await using var scope2 = _services.CreateAsyncScope();
        var svc2 = scope2.ServiceProvider.GetRequiredService<IBackupSetService>();

        // 不指定状态 = 只看还活着的。磁盘上早就没有的那一份不该再进份数和容量，
        // 否则一个一份都不剩的任务在归拢行上仍然报「有备份、占了多少 GB」。
        var defaultList = await svc2.GetListAsync(new BackupQuery { ClientId = clientId });
        Assert.Empty(defaultList.Items);

        var groups = await svc2.GetGroupsAsync(new BackupQuery { ClientId = clientId });
        Assert.Empty(groups.Items);

        // 记录还在：显式挑「已删除」这一档就能查到那一份去哪了。
        var deletedList = await svc2.GetListAsync(new BackupQuery { ClientId = clientId, Status = "deleted" });
        Assert.Equal(setId, Assert.Single(deletedList.Items).Id);
    }

    // ---------- 基础设施 ----------

    private string MakeRepositoryDirectory()
    {
        var dir = Path.Combine(_repositoryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "data.bin"), "recycle-test");
        return dir;
    }

    private async Task SetRepositoryRootSettingAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE system_settings
            SET setting_value = to_jsonb({_repositoryRoot}::text)
            WHERE setting_key = 'repository_path'
            """);
    }

    private async Task<(Guid SetId, Guid ClientId)> SeedAsync(bool locked = false, string? repositoryPath = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var policy = new RetentionPolicy
        {
            Id = Guid.NewGuid(),
            Name = $"recycle-{suffix[..12]}",
            KeepLastCount = 7,
            MinimumRetentionDays = 0,
            RecycleBinDays = 7,
            CreatedAt = now,
            UpdatedAt = now
        };
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"rec-{suffix}",
            Hostname = $"rec-host-{suffix[..8]}",
            DisplayName = $"回收站测试-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"rec-task-{suffix[..8]}",
            ApplicationName = "RecycleTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            RetentionPolicyId = policy.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            TaskId = task.Id,
            CandidateKey = $"rec-{suffix}",
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

        db.RetentionPolicies.Add(policy);
        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        db.CandidateBackupSets.Add(candidate);
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync();

        var set = new BackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            TaskId = task.Id,
            SourceCandidateId = candidate.Id,
            UploadSessionId = session.Id,
            BackupSetCode = $"BS-REC-{suffix[..12]}",
            Status = BackupSetStatus.Available,
            DiscoveredAt = now,
            UploadedAt = now,
            VerifiedAt = now,
            RepositoryPath = repositoryPath,
            TotalFiles = 1,
            TotalBytes = 12,
            Locked = locked,
            CreatedAt = now
        };
        db.BackupSets.Add(set);
        await db.SaveChangesAsync();

        return (set.Id, client.Id);
    }
}
