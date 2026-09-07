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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 入库阶段的整文件哈希复核（实施方案 T2 / T3）。
///
/// 复核原先在 CompleteFileAsync 里，也就是客户端同步等待的请求路径上：
/// 6GB 的备份在慢盘上要整读几分钟，而客户端的 HTTP 超时是 10 分钟——
/// 正常上传会以超时告终，而报出来的错跟哈希毫无关系。挪到入库阶段之后，
/// **判定口径一个字没变**，变的只是「谁在等」。这组测试盯的就是「一个字没变」。
/// </summary>
[Collection("postgres")]
public class UploadCommitHashVerificationTests : IDisposable
{
    private static readonly byte[] Payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
    private const int ChunkSize = 16;
    private const int ChunkCount = 3;

    private readonly PostgresDatabaseFixture _fixture;
    private readonly string _root;

    public UploadCommitHashVerificationTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        _root = Path.Combine(Path.GetTempPath(), "bm-commit-hash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "staging"));
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    [Fact]
    public async Task 哈希一致时入库并置为已校验()
    {
        await using var sp = BuildServices();
        var (sessionId, _) = await SeedVerifyingSessionAsync(sp);

        await CommitAsync(sp, sessionId);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.UploadSessions.AsNoTracking().Include(s => s.Files).FirstAsync(s => s.Id == sessionId);

        Assert.Equal(UploadStatus.Committed, session.Status);
        Assert.All(session.Files, f => Assert.Equal(UploadFileStatus.Verified, f.Status));
        Assert.All(session.Files, f => Assert.Equal(FullHash(), f.ServerSha256));

        // T3：verified_bytes 由入库时一次算出，而不是每完成一个文件把全表 SUM 一遍。
        Assert.Equal(Payload.Length, session.VerifiedBytes);
    }

    [Fact]
    public async Task 暂存文件被篡改时以哈希错误失败且仓库不留半份()
    {
        await using var sp = BuildServices();
        var (sessionId, fileId) = await SeedVerifyingSessionAsync(sp);

        // 分块都收齐、complete-file 也过了，此刻篡改暂存文件——
        // 这正是「上传过程中源文件被改」在服务端的等效形态，也是整文件复核唯一还在覆盖的场景。
        var staged = Path.Combine(_root, "staging", "sessions", sessionId.ToString("N"), $"{fileId:N}.part");
        Assert.True(File.Exists(staged));
        var tampered = Payload.ToArray();
        tampered[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(staged, tampered);

        await CommitAsync(sp, sessionId);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.UploadSessions.AsNoTracking().Include(s => s.Files).FirstAsync(s => s.Id == sessionId);

        // 错误码必须是 FILE_HASH_MISMATCH 而不是 COMMIT_FAILED：
        // 前者说「传上来的东西就不对」，正确处置是重传；
        // 后者说「东西是对的，存进仓库时出了问题」，重传没用、该查磁盘。
        // 两者混成一个码，现场只能靠猜。
        Assert.Equal(UploadStatus.Failed, session.Status);
        Assert.Equal("FILE_HASH_MISMATCH", session.ErrorCode);
        Assert.All(session.Files, f => Assert.Equal(UploadFileStatus.Failed, f.Status));
        Assert.All(session.Files, f => Assert.Equal("FILE_HASH_MISMATCH", f.ErrorCode));

        // 失败必须发告警：备份没存进去这件事不能只留在日志里。
        Assert.True(await db.Alerts.AsNoTracking()
            .AnyAsync(a => a.AlertKey == $"session:{sessionId}:commit_failed"));

        // 复核排在创建任何仓库目录之前，因此仓库里不该出现半份东西。
        var repo = Path.Combine(_root, "repo");
        Assert.Empty(Directory.EnumerateFileSystemEntries(repo, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 客户端声明哈希与实测不一致时同样失败()
    {
        // 权威基准是 expected_sha256（预检清单，服务端持有），客户端声明值只作交叉验证。
        // 但「只作交叉验证」不等于「可以不验」——原先那版「不传就整段跳过」，
        // 等于把完整性开关交到了被验证方手里。挪到入库之后这条语义必须还在。
        await using var sp = BuildServices();
        var (sessionId, _) = await SeedVerifyingSessionAsync(sp, declaredSha: new string('f', 64));

        await CommitAsync(sp, sessionId);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.UploadSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId);

        Assert.Equal(UploadStatus.Failed, session.Status);
        Assert.Equal("FILE_HASH_MISMATCH", session.ErrorCode);
        Assert.Contains("客户端声明哈希", session.ErrorMessage);
    }

    // ---------- 装配 ----------

    private static async Task CommitAsync(ServiceProvider sp, Guid sessionId)
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
            scope.ServiceProvider.GetRequiredService<IUploadStorage>(),
            sessionId,
            CancellationToken.None);
    }

    private ServiceProvider BuildServices()
    {
        // 存储根从 system_settings 走（与 UploadCommitClosureTests 同一套）：
        // UploadStorage 的解析顺序是库 → appsettings → 兜底，只塞配置的话拿到的仍是库里的旧值。
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

    /// <summary>走真实上传协议造一个 verifying 状态、暂存文件齐备的会话。</summary>
    private async Task<(Guid SessionId, Guid FileId)> SeedVerifyingSessionAsync(
        ServiceProvider sp, string? declaredSha = null)
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
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = declaredSha ?? FullHash() });
        await svc.CompleteSessionAsync(clientId, created.UploadSessionId, new CompleteUploadSessionRequest
        {
            TotalFiles = 1,
            TotalBytes = Payload.Length
        });

        return (created.UploadSessionId, fileId);
    }

    private static async Task<(Guid ClientId, Guid CandidateId)> SeedAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"itch-{suffix}",
            Hostname = $"itch-host-{suffix[..8]}",
            DisplayName = $"复核测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"复核测试任务-{suffix[..8]}",
            ApplicationName = "test",
            SourcePath = @"C:\bm-test",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var candidate = new CandidateBackupSet
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            TaskId = task.Id,
            CandidateKey = $"itch-{suffix}",
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
        var offset = index * ChunkSize;
        return Payload.Skip(offset).Take(Math.Min(ChunkSize, Payload.Length - offset)).ToArray();
    }

    private static string ChunkHash(int index) =>
        Convert.ToHexString(SHA256.HashData(ChunkBytes(index))).ToLowerInvariant();

    private static string FullHash() =>
        Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
}
