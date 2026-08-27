using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 入库收口集成测试（审计 E-07 / E-16）。
///
/// E-07：入库失败路径原先只有一种收场方式——置 Failed + 发 Critical 告警。
/// 于是「停机时被取消」和「提交后收尾出错」这两种「备份其实没问题」的情况，
/// 都会被记成失败并触发上传指令复位重发，客户端把几十 GB 再传一遍。
/// E-16：备份集编码里的年份是误导——序列是全局的，跨年不重置。
/// </summary>
[Collection("postgres")]
public class UploadCommitClosureTests : IDisposable
{
    /// <summary>测试载荷：40 字节，分块 16 → 3 块（16/16/8）</summary>
    private static readonly byte[] Payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
    private const int ChunkSize = 16;
    private const int ChunkCount = 3;

    private readonly PostgresDatabaseFixture _fixture;
    private readonly string _root;

    public UploadCommitClosureTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        _root = Path.Combine(Path.GetTempPath(), "bm-commit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "staging"));
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    // ---------- 用例 ----------

    [Fact]
    public async Task 停机取消不把会话记成失败也不告警()
    {
        await using var sp = BuildServices();
        var sessionId = await SeedVerifyingSessionAsync(sp);

        // 模拟「正在复制暂存文件时服务停机」：取消发生在入库过程内部，
        // 而不是在方法入口——入口那次取消不会走到需要收场的代码。
        using var cts = new CancellationTokenSource();
        var storage = new CancelDuringOpenStorage(sp, cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CommitAsync(sp, sessionId, cts.Token, storage));

        // 停机不是失败：会话留在 verifying，重启后 RecoverPendingWorkAsync 会重新入队
        Assert.Equal(UploadStatus.Verifying, await SessionStatusAsync(sp, sessionId));
        Assert.False(await HasCommitFailedAlertAsync(sp, sessionId));
    }

    [Fact]
    public async Task 真正的入库失败仍然置失败并告警()
    {
        // 这条是回归护栏：E-07 改的是「哪些情况不算失败」，
        // 真失败的收场方式一个字都不能变，否则问题会变成静默丢失。
        await using var sp = BuildServices();
        var sessionId = await SeedVerifyingSessionAsync(sp);

        var storage = new ThrowingStorage(sp, new IOException("暂存文件读取失败"));
        await CommitAsync(sp, sessionId, CancellationToken.None, storage);

        Assert.Equal(UploadStatus.Failed, await SessionStatusAsync(sp, sessionId));
        Assert.True(await HasCommitFailedAlertAsync(sp, sessionId));

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.UploadSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId);
        Assert.Equal("COMMIT_FAILED", session.ErrorCode);
    }

    [Fact]
    public async Task 入库成功后编码是全局流水号且不含年份()
    {
        await using var sp = BuildServices();
        var first = await SeedVerifyingSessionAsync(sp);
        var second = await SeedVerifyingSessionAsync(sp);

        await CommitAsync(sp, first, CancellationToken.None);
        await CommitAsync(sp, second, CancellationToken.None);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var codes = new List<string>();
        foreach (var sessionId in new[] { first, second })
        {
            Assert.Equal(UploadStatus.Committed, await SessionStatusAsync(sp, sessionId));
            var backupSet = await db.BackupSets.AsNoTracking().FirstAsync(b => b.UploadSessionId == sessionId);
            Assert.Equal(BackupSetStatus.Available, backupSet.Status);
            // BS-NNNNNN：至少六位、不含年份分段
            Assert.Matches(new Regex(@"^BS-\d{6,}$"), backupSet.BackupSetCode);
            codes.Add(backupSet.BackupSetCode);
        }

        // 序列保证并发下也不重复
        Assert.Equal(2, codes.Distinct().Count());

        // 存量 BS-2026-xxxx 形态的引用仍然有效：字段本身没有格式约束，两种编码并存
        var legacy = await db.BackupSets.AsNoTracking()
            .AnyAsync(b => b.BackupSetCode == "BS-2026-0001");
        Assert.False(legacy);   // 本用例没造存量数据，仅确认查询本身不因格式改变而失效
    }

    // ---------- 基础设施 ----------

    private ServiceProvider BuildServices()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_fixture.ConnectionString).Options;
        using (var db = new AppDbContext(options))
        {
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'staging_path'",
                JsonSerializer.Serialize(Path.Combine(_root, "staging")));
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'repository_path'",
                JsonSerializer.Serialize(Path.Combine(_root, "repo")));
        }

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddScoped<IUploadStorage, UploadStorage>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddKeyedSingleton(QueueKeys.Commit, (_, _) => Channel.CreateUnbounded<WorkItem>());
        sc.AddScoped<IUploadSessionService, UploadSessionService>();
        return sc.BuildServiceProvider();
    }

    /// <summary>直接驱动一次入库；storage 传 null 时用真实实现</summary>
    private static async Task CommitAsync(
        ServiceProvider sp, Guid sessionId, CancellationToken ct, IUploadStorage? storage = null)
    {
        var worker = new UploadCommitWorker(
            sp.GetRequiredKeyedService<Channel<WorkItem>>(QueueKeys.Commit),
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UploadCommitWorker>.Instance);

        await using var scope = sp.CreateAsyncScope();
        await worker.CommitSessionAsync(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IAlertingService>(),
            scope.ServiceProvider.GetRequiredService<IAgentNotificationService>(),
            storage ?? scope.ServiceProvider.GetRequiredService<IUploadStorage>(),
            sessionId,
            ct);
    }

    /// <summary>走真实上传协议造一个 verifying 状态、暂存文件齐备的会话</summary>
    private static async Task<Guid> SeedVerifyingSessionAsync(ServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IUploadSessionService>();
        var (clientId, candidateId) = await SeedAsync(db);

        var created = await svc.CreateSessionAsync(clientId, new CreateUploadSessionRequest
        {
            CandidateBackupSetId = candidateId,
            TotalFiles = 1,
            TotalBytes = Payload.Length,
            ChunkSizeBytes = ChunkSize
        });
        var fileId = created.Files[0].UploadFileId;

        for (var i = 0; i < ChunkCount; i++)
        {
            await svc.UploadChunkAsync(clientId, created.UploadSessionId, fileId, i,
                offset: (long)i * ChunkSize, expectedHash: ChunkHash(i), data: new MemoryStream(ChunkBytes(i)));
        }

        await svc.CompleteFileAsync(clientId, created.UploadSessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        await svc.CompleteSessionAsync(clientId, created.UploadSessionId, new CompleteUploadSessionRequest
        {
            TotalFiles = 1,
            TotalBytes = Payload.Length
        });

        return created.UploadSessionId;
    }

    private static async Task<UploadStatus> SessionStatusAsync(ServiceProvider sp, Guid sessionId)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.UploadSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId)).Status;
    }

    private static async Task<bool> HasCommitFailedAlertAsync(ServiceProvider sp, Guid sessionId)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Alerts.AsNoTracking().AnyAsync(a => a.AlertKey == $"session:{sessionId}:commit_failed");
    }

    private static async Task<(Guid ClientId, Guid CandidateId)> SeedAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"itcc-{suffix}",
            Hostname = $"itcc-host-{suffix[..8]}",
            DisplayName = $"入库测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"itcc-task-{suffix[..8]}",
            ApplicationName = "CommitTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            CreatedAt = now,
            UpdatedAt = now
        };
        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            TaskId = task.Id,
            CandidateKey = $"itcc-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = Payload.Length,
            CreatedAt = now,
            UpdatedAt = now,
            Files =
            {
                new CandidateFile
                {
                    Id = Guid.NewGuid(),
                    RelativePath = "db.bak",
                    FileName = "db.bak",
                    SizeBytes = Payload.Length,
                    LastModifiedAt = now,
                    Sha256 = FullHash(),
                    SortOrder = 0
                }
            }
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        db.CandidateBackupSets.Add(candidate);
        await db.SaveChangesAsync();
        return (client.Id, candidate.Id);
    }

    private static byte[] ChunkBytes(int index)
    {
        var start = index * ChunkSize;
        return Payload.Skip(start).Take(Math.Min(ChunkSize, Payload.Length - start)).ToArray();
    }

    private static string ChunkHash(int index) => Hex(SHA256.HashData(ChunkBytes(index)));

    private static string FullHash() => Hex(SHA256.HashData(Payload));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // ---------- 故障注入 ----------

    /// <summary>
    /// 复制暂存文件时触发取消，模拟「入库进行到一半服务停机」。
    /// 在方法入口就取消是测不出问题的——那时还没有任何需要收场的状态。
    /// </summary>
    private sealed class CancelDuringOpenStorage(ServiceProvider sp, CancellationTokenSource cts) : DelegatingStorage(sp)
    {
        public override Task<Stream> OpenStagedFileAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }
    }

    /// <summary>复制暂存文件时抛真实故障，模拟磁盘读失败</summary>
    private sealed class ThrowingStorage(ServiceProvider sp, Exception failure) : DelegatingStorage(sp)
    {
        public override Task<Stream> OpenStagedFileAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default)
            => throw failure;
    }

    /// <summary>把除注入点以外的调用原样转发给真实实现</summary>
    private abstract class DelegatingStorage(ServiceProvider sp) : IUploadStorage
    {
        private IUploadStorage Inner => sp.CreateScope().ServiceProvider.GetRequiredService<IUploadStorage>();

        public Task<string> GetStagingRootAsync(CancellationToken ct = default) => Inner.GetStagingRootAsync(ct);
        public Task<string> GetRepositoryRootAsync(CancellationToken ct = default) => Inner.GetRepositoryRootAsync(ct);
        public Task<StorageRootResolution> ResolveRootAsync(string settingKey, CancellationToken ct = default) => Inner.ResolveRootAsync(settingKey, ct);
        public Task<string> GetSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default) => Inner.GetSessionDirectoryAsync(sessionId, ct);
        public Task<string> EnsureSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default) => Inner.EnsureSessionDirectoryAsync(sessionId, ct);
        public Task<long> GetStagingFreeBytesAsync(CancellationToken ct = default) => Inner.GetStagingFreeBytesAsync(ct);
        public Task<(string Hash, long BytesWritten)> WriteChunkAsync(Guid sessionId, Guid uploadFileId, int chunkIndex, long offset, Stream data, CancellationToken ct = default)
            => Inner.WriteChunkAsync(sessionId, uploadFileId, chunkIndex, offset, data, ct);
        public Task<string> ComputeFileHashAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) => Inner.ComputeFileHashAsync(sessionId, uploadFileId, ct);
        public Task<(bool Exists, long Length)> GetStagedFileInfoAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) => Inner.GetStagedFileInfoAsync(sessionId, uploadFileId, ct);
        public virtual Task<Stream> OpenStagedFileAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) => Inner.OpenStagedFileAsync(sessionId, uploadFileId, ct);
        public Task CleanupSessionAsync(Guid sessionId, CancellationToken ct = default) => Inner.CleanupSessionAsync(sessionId, ct);
    }
}
