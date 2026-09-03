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
/// 保留策略的「空策略」防线（D1）。
///
/// 四项份数全空 + 最短保留天数 0 是一份能通过全部单字段校验的策略，
/// 而它的含义是「这个策略下的每一份备份都要删」——表单里那四项折叠在高级选项、默认留空，
/// 也就是说照默认值点保存就能配出它。这里证明两道防线都在：
/// 写入侧拒绝它；万一库里已经有了，清理侧也不会把备份清空。
/// </summary>
[Collection("postgres")]
public class RetentionPolicyValidationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;
    private string _repositoryRoot = null!;

    public RetentionPolicyValidationTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "bm-retval-test", Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(_repositoryRoot);

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<IScheduledLockService, ScheduledLockService>();
        sc.AddScoped<IUploadStorage, UploadStorage>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddScoped<IRetentionPolicyService, RetentionPolicyService>();
        _services = sc.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE system_settings
            SET setting_value = to_jsonb({_repositoryRoot}::text)
            WHERE setting_key = 'repository_path'
            """);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- 写入侧 ----------

    [Fact]
    public async Task 四项份数全空的策略不允许创建()
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IRetentionPolicyService>();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => svc.CreateAsync(new RetentionPolicyUpsertDto
        {
            Name = $"空策略-{Guid.NewGuid():N}",
            RecycleBinDays = 7
        }));

        Assert.Contains("至少要填一项", ex.Message);
    }

    /// <summary>
    /// 回收站 0 天 = 移进回收站的下一轮就物理删除，没有撤销窗口。
    /// 它与「份数全空」是同一类错误的两半：一个决定删多少，一个决定还能不能捞回来。
    /// </summary>
    [Fact]
    public async Task 回收站保留天数为零的策略不允许创建()
    {
        await using var scope = _services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IRetentionPolicyService>();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => svc.CreateAsync(new RetentionPolicyUpsertDto
        {
            Name = $"零回收站-{Guid.NewGuid():N}",
            KeepLastCount = 3,
            RecycleBinDays = 0
        }));

        Assert.Contains("回收站保留天数", ex.Message);
    }

    // ---------- 运行期兜底 ----------

    /// <summary>
    /// 库里已经存在一份空策略时（校验落地之前存进去的，或直接改库），
    /// 清理不能按字面含义把这个任务的备份清空：按「每个业务单元保留最新一份」降级，
    /// 并留下一条 Critical 告警说明策略本身有问题。
    /// </summary>
    [Fact]
    public async Task 存量空策略清理时只保留最新一份并告警()
    {
        var now = DateTime.UtcNow;
        var policyId = Guid.NewGuid();
        Guid newestId, middleId, oldestId, taskId;

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 刻意绕过 RetentionPolicyService 直接落库——这一条测的正是「校验挡不住的存量数据」
            var policy = new RetentionPolicy
            {
                Id = policyId,
                Name = $"存量空策略-{policyId:N}",
                MinimumRetentionDays = 0,
                RecycleBinDays = 7,
                CreatedAt = now,
                UpdatedAt = now
            };

            var (clientId, seededTaskId) = await SeedClientAndTaskAsync(db, policy, now);
            taskId = seededTaskId;

            oldestId = await SeedAvailableSetAsync(db, clientId, taskId, now.AddDays(-30), now);
            middleId = await SeedAvailableSetAsync(db, clientId, taskId, now.AddDays(-20), now);
            newestId = await SeedAvailableSetAsync(db, clientId, taskId, now.AddDays(-10), now);
        }

        await RunWorkerUntilAsync(async () =>
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await StatusAsync(db, oldestId) == BackupSetStatus.RecycleBin
                && await StatusAsync(db, middleId) == BackupSetStatus.RecycleBin;
        });

        await using var verify = _services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        // 最新一份必须还在：「宁可清理不干净，不可清空」
        Assert.Equal(BackupSetStatus.Available, await StatusAsync(verifyDb, newestId));

        var alert = await verifyDb.Alerts.AsNoTracking()
            .SingleAsync(a => a.AlertKey == $"retention_policy:{policyId}:invalid");
        Assert.Equal(AlertLevel.Critical, alert.Level);
        Assert.Equal("retention_policy_invalid", alert.Category);
    }

    // ---------- 基础设施 ----------

    private async Task RunWorkerUntilAsync(Func<Task<bool>> assertion, int timeoutSeconds = 30)
    {
        var worker = new RetentionCleanupWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<RetentionCleanupWorker>>());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (await assertion())
                    return;
                await Task.Delay(500);
            }

            Assert.True(await assertion(), "等待清理结果超时，期望状态未出现");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<BackupSetStatus> StatusAsync(AppDbContext db, Guid id)
        => (await db.BackupSets.AsNoTracking().SingleAsync(s => s.Id == id)).Status;

    private static async Task<(Guid ClientId, Guid TaskId)> SeedClientAndTaskAsync(
        AppDbContext db, RetentionPolicy policy, DateTime now)
    {
        db.RetentionPolicies.Add(policy);

        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"retval-{suffix}",
            Hostname = $"retval-host-{suffix[..8]}",
            DisplayName = $"空策略测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"retval-task-{suffix[..8]}",
            ApplicationName = "RetentionValidationTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            RetentionPolicyId = policy.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return (client.Id, task.Id);
    }

    private static async Task<Guid> SeedAvailableSetAsync(
        AppDbContext db, Guid clientId, Guid taskId, DateTime uploadedAt, DateTime now)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateKey = $"retval-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = uploadedAt,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = 12,
            CreatedAt = uploadedAt,
            UpdatedAt = uploadedAt
        };
        db.CandidateBackupSets.Add(candidate);
        await db.SaveChangesAsync();

        var session = new BackupMonitor.Core.Entities.Upload.UploadSession
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateBackupSetId = candidate.Id,
            Status = UploadStatus.Committed,
            TotalFiles = 1,
            TotalBytes = 12,
            StartedAt = uploadedAt,
            LastActivityAt = uploadedAt,
            CompletedAt = uploadedAt,
            CommittedAt = uploadedAt,
            CreatedAt = uploadedAt,
            UpdatedAt = uploadedAt
        };
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync();

        var set = new BackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            SourceCandidateId = candidate.Id,
            UploadSessionId = session.Id,
            BackupSetCode = $"BS-RV-{suffix[..12]}",
            Status = BackupSetStatus.Available,
            DiscoveredAt = uploadedAt,
            UploadedAt = uploadedAt,
            VerifiedAt = uploadedAt,
            TotalFiles = 1,
            TotalBytes = 12,
            CreatedAt = uploadedAt
        };
        db.BackupSets.Add(set);
        await db.SaveChangesAsync();
        return set.Id;
    }
}
