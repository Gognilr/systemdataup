using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 删除备份任务的回归测试。
///
/// 背景一：commands 和 alerts 都以 ON DELETE NO ACTION 指向 backup_tasks。
/// 删除逻辑原先只把 pending 指令改成 cancelled——行仍然留在表里，
/// 于是任何被点过一次预检的任务在删除时都会撞上外键约束，
/// 异常兜底成 500「服务器内部错误，请稍后重试」，而重试永远不会成功。
///
/// 背景二（D4）：三道守卫（有备份集 / 有候选 / 有历史会话）都没有状态过滤，
/// 也都没有可满足的路径——候选备份集全系统没有删除入口，历史会话永久保留，
/// 而备份集是软删、行永远在。合起来的效果是「成功备份过一次的任务永远删不掉」，
/// 提示还写着「请先处理保留策略」，指向一条走不通的路。
/// 现在只有「还活着的备份版本」挡删除，其余附属记录随任务级联清理。
/// </summary>
[Collection("postgres")]
public class BackupTaskDeletionTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;
    private string _repositoryRoot = null!;

    public BackupTaskDeletionTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "bm-taskdel-test", Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(_repositoryRoot);

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        // 删除路径不下发指令，桩件即可；引真实实现会把 CommandSigner 等一串依赖拖进来。
        sc.AddScoped<ICommandDispatcher, UnusedCommandDispatcher>();
        sc.AddScoped<IAgentConfigService, AgentConfigService>();
        sc.AddScoped<SystemSettingsProvider>();
        sc.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        // D4：删除任务要清掉回收站里那些备份集的仓库目录，围栏以仓库根为准
        sc.AddScoped<IUploadStorage, UploadStorage>();
        sc.AddScoped<IBackupTaskService, BackupTaskService>();
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
    /// D4：候选备份集不再挡住删除。
    ///
    /// 这条断言原先反过来——「有候选就 409」。改回来的理由不是放宽守卫，
    /// 而是那道守卫没有可满足的路径：候选备份集全系统没有任何删除入口，
    /// 于是「先把候选清掉再删任务」这句提示做不到，任何点过一次预检的任务永远删不掉。
    /// 候选是任务的附属记录，任务没了它也失去意义，随任务一并清理。
    /// </summary>
    [Fact]
    public async Task 有候选备份集的任务可以删除并清掉候选()
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
            await scope.ServiceProvider.GetRequiredService<IBackupTaskService>().DeleteAsync(taskId);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.BackupTasks.AnyAsync(t => t.Id == taskId));
            Assert.False(await db.CandidateBackupSets.AnyAsync(c => c.TaskId == taskId));
        }
    }

    /// <summary>
    /// 还活着的备份版本仍然必须挡住删除，并且要给出 409 和一条走得通的提示——
    /// 「删不掉」和「服务器故障」是两回事，放宽其余守卫不能把这一条一起放掉。
    /// </summary>
    [Fact]
    public async Task 有可用备份版本的任务仍然拒绝删除()
    {
        var (clientId, taskId) = await CreateTaskAsync();
        await SeedBackupSetAsync(clientId, taskId, BackupSetStatus.Available, repositoryPath: null);

        await using var scope = _services.CreateAsyncScope();
        var tasks = scope.ServiceProvider.GetRequiredService<IBackupTaskService>();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => tasks.DeleteAsync(taskId));

        Assert.Equal(409, ex.StatusCode);
        // 原先的提示是「请先处理保留策略」，那条路走不通（清完回收站行照样在）。
        // 现在必须指向一个真的能做到的动作。
        Assert.Contains("备份集", ex.Message);
    }

    /// <summary>
    /// D4：回收站里的备份集不挡删除，行与磁盘目录都要跟着清掉。
    /// 留下目录的话只能等 RepositoryReconcileWorker 事后报成孤儿，那是事后补救。
    /// </summary>
    [Fact]
    public async Task 备份集在回收站的任务可以删除并清掉仓库目录()
    {
        var (clientId, taskId) = await CreateTaskAsync();

        var repositoryPath = Path.Combine(_repositoryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "data.bin"), "task-delete-test");

        var setId = await SeedBackupSetAsync(clientId, taskId, BackupSetStatus.RecycleBin, repositoryPath);

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupTaskService>().DeleteAsync(taskId);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.BackupTasks.AnyAsync(t => t.Id == taskId));
            Assert.False(await db.BackupSets.AnyAsync(s => s.Id == setId));
            Assert.False(await db.UploadSessions.AnyAsync(s => s.TaskId == taskId));
            Assert.False(await db.CandidateBackupSets.AnyAsync(c => c.TaskId == taskId));
        }

        Assert.False(Directory.Exists(repositoryPath));
    }

    /// <summary>
    /// 仓库根解析不了时，任务仍然要被完整删除，不能留下半截状态。
    ///
    /// 级联清理是九步独立的 DELETE/UPDATE，而目录删除原先夹在第五步——
    /// 也就是说 GetRepositoryRootAsync 一抛（盘掉了、路径没权限），
    /// 前四步已经把告警、指令、执行项、恢复请求删掉了，任务行却还在。
    /// 这个半截状态没有任何一条路径能修回去。
    ///
    /// 现在库里的九步在一个事务里，目录删除挪到提交之后并自己吞掉异常：
    /// 删不掉的目录由 RepositoryReconcileWorker 报成孤儿目录，有人工处置的路径。
    /// </summary>
    [Fact]
    public async Task 仓库根解析失败时任务仍被完整删除且不留半截状态()
    {
        var (clientId, taskId) = await CreateTaskAsync();

        var repositoryPath = Path.Combine(_repositoryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "data.bin"), "task-delete-test");

        var setId = await SeedBackupSetAsync(clientId, taskId, BackupSetStatus.RecycleBin, repositoryPath);

        // 只替换存储实现，其余依赖与正常路径完全一致
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<ICommandDispatcher, UnusedCommandDispatcher>();
        sc.AddScoped<IAgentConfigService, AgentConfigService>();
        sc.AddScoped<SystemSettingsProvider>();
        sc.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        sc.AddScoped<IUploadStorage, ThrowingUploadStorage>();
        sc.AddScoped<IBackupTaskService, BackupTaskService>();
        await using var broken = sc.BuildServiceProvider();

        await using (var scope = broken.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupTaskService>().DeleteAsync(taskId);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.BackupTasks.AnyAsync(t => t.Id == taskId));
            Assert.False(await db.BackupSets.AnyAsync(s => s.Id == setId));
            Assert.False(await db.UploadSessions.AnyAsync(s => s.TaskId == taskId));
            Assert.False(await db.CandidateBackupSets.AnyAsync(c => c.TaskId == taskId));
            Assert.False(await db.Commands.AnyAsync(c => c.TaskId == taskId));
        }

        // 目录删不掉是可接受的结局（仓库对帐会报出来），删了半个任务不是
        Assert.True(Directory.Exists(repositoryPath));
    }

    /// <summary>种子：候选 + 上传会话 + 指定状态的备份集（三者的外键链条正是级联顺序要处理的）</summary>
    private async Task<Guid> SeedBackupSetAsync(
        Guid clientId, Guid taskId, BackupSetStatus status, string? repositoryPath)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CandidateKey = $"taskdel-{suffix}",
            SourceRoot = @"D:\backup\test",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = 16,
            CreatedAt = now,
            UpdatedAt = now
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
            TotalBytes = 16,
            StartedAt = now,
            LastActivityAt = now,
            CompletedAt = now,
            CommittedAt = now,
            CreatedAt = now,
            UpdatedAt = now
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
            BackupSetCode = $"BS-TD-{suffix[..12]}",
            Status = status,
            DiscoveredAt = now,
            UploadedAt = now,
            RepositoryPath = repositoryPath,
            TotalFiles = 1,
            TotalBytes = 16,
            CreatedAt = now
        };
        db.BackupSets.Add(set);
        await db.SaveChangesAsync();
        return set.Id;
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
        bool restartIfNotActive = false,
        CancellationToken ct = default) =>
        throw new NotSupportedException("删除备份任务不应下发指令");
}

/// <summary>
/// 仓库根解析必定失败的存储桩件：模拟盘掉了 / 路径没权限。
/// 删除路径只会用到 GetRepositoryRootAsync，其余成员真被调用说明测试假设已经不成立。
/// </summary>
internal sealed class ThrowingUploadStorage : IUploadStorage
{
    private static Exception Unavailable() => new IOException("仓库根不可用（测试桩件）");

    public Task<string> GetRepositoryRootAsync(CancellationToken ct = default) => throw Unavailable();
    public Task<string> GetStagingRootAsync(CancellationToken ct = default) => throw Unavailable();
    public Task<StorageRootResolution> ResolveRootAsync(string settingKey, CancellationToken ct = default) => throw Unavailable();
    public Task<string> GetSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default) => throw Unavailable();
    public Task<string> EnsureSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default) => throw Unavailable();
    public Task<long> GetStagingFreeBytesAsync(CancellationToken ct = default) => throw Unavailable();
    public Task<(string Hash, long BytesWritten)> WriteChunkAsync(
        Guid sessionId, Guid uploadFileId, int chunkIndex, long offset, Stream data, CancellationToken ct = default)
        => throw Unavailable();
    public Task<string> ComputeFileHashAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) => throw Unavailable();
    public Task<(bool Exists, long Length)> GetStagedFileInfoAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) => throw Unavailable();
    public Task<Stream> OpenStagedFileAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) => throw Unavailable();
    public Task<string> GetStagedFilePathAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) => throw Unavailable();
    public Task CleanupSessionAsync(Guid sessionId, CancellationToken ct = default) => throw Unavailable();
}
