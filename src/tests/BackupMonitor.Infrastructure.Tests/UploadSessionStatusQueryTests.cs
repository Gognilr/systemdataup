using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 会话状态查询的往返次数（审计 H-18）。
///
/// GetSessionStatusAsync 原先对每个 pending/uploading 的文件单独查一次分块表——
/// 一个 500 文件的会话就是 500 次数据库往返，而这个接口是 Agent 断点续传时
/// 必调的第一步。改成一次性按 UploadFileId 分组查回来，内存里分配给各文件。
/// </summary>
[Collection("postgres")]
public class UploadSessionStatusQueryTests : IDisposable
{
    private const int FileCount = 50;
    private const int ChunkSize = 16;
    private static readonly byte[] Payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();

    private readonly PostgresDatabaseFixture _fixture;
    private readonly string _stagingRoot;
    private readonly CommandCountingInterceptor _counter = new();

    public UploadSessionStatusQueryTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        _stagingRoot = Path.Combine(Path.GetTempPath(), "bm-status-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_stagingRoot, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    [Fact]
    public async Task 多文件会话的状态查询只走个位数往返()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IUploadSessionService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var (clientId, candidateId) = await SeedAsync(db);
        var created = await svc.CreateSessionAsync(clientId, new CreateUploadSessionRequest
        {
            CandidateBackupSetId = candidateId,
            TotalFiles = FileCount,
            TotalBytes = FileCount * Payload.Length,
            ChunkSizeBytes = ChunkSize
        });
        Assert.Equal(FileCount, created.Files.Count);

        // 给一部分文件传一块，让它们处于 uploading——正是原先每个都要单独查一次的那些
        foreach (var file in created.Files.Take(FileCount / 2))
        {
            await svc.UploadChunkAsync(clientId, created.UploadSessionId, file.UploadFileId, 0,
                offset: 0, expectedHash: ChunkHash(0), data: new MemoryStream(ChunkBytes(0)));
        }

        _counter.Reset();
        var status = await svc.GetSessionStatusAsync(clientId, created.UploadSessionId);

        Assert.Equal(FileCount, status.Files.Count);
        // 会话 + 文件列表 + 分块，三次查询量级；原先是 1 + 1 + 文件数
        Assert.True(_counter.Count <= 5,
            $"状态查询走了 {_counter.Count} 次数据库往返，应当与文件数无关（本例 {FileCount} 个文件）");
    }

    // ---------- 基础设施 ----------

    private ServiceProvider BuildServices()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_fixture.ConnectionString).Options;
        using (var db = new AppDbContext(options))
        {
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'staging_path'",
                JsonSerializer.Serialize(_stagingRoot));
        }

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString).AddInterceptors(_counter));
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddScoped<IUploadStorage, UploadStorage>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddKeyedSingleton(QueueKeys.Commit, (_, _) => Channel.CreateUnbounded<WorkItem>());
        sc.AddScoped<IUploadSessionService, UploadSessionService>();
        return sc.BuildServiceProvider();
    }

    /// <summary>数一数这段代码实际发了多少条 SQL——N+1 只有真数才看得见</summary>
    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<(Guid ClientId, Guid CandidateId)> SeedAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"itsq-{suffix}",
            Hostname = $"itsq-host-{suffix[..8]}",
            DisplayName = $"状态查询测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"itsq-task-{suffix[..8]}",
            ApplicationName = "StatusTest",
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
            CandidateKey = $"itsq-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = FileCount,
            TotalBytes = FileCount * Payload.Length,
            CreatedAt = now,
            UpdatedAt = now
        };

        for (var i = 0; i < FileCount; i++)
        {
            candidate.Files.Add(new CandidateFile
            {
                Id = Guid.NewGuid(),
                RelativePath = $"part-{i:000}.bak",
                FileName = $"part-{i:000}.bak",
                SizeBytes = Payload.Length,
                LastModifiedAt = now,
                Sha256 = Hex(SHA256.HashData(Payload)),
                SortOrder = i
            });
        }

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

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
