using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 上传断点续传集成测试（设计书 14 上传协议 / DEV-PROMPTS 提示词 3a）：
/// 缺块查询、乱序上传、重复上传同一块、块哈希不匹配拒收、整文件哈希比对、跨会话（重启）续传。
/// 在 Testcontainers 真实库 + 真实 UploadStorage 磁盘暂存上运行，不使用内存库。
/// </summary>
[Collection("postgres")]
public class UploadSessionResumeTests : IDisposable
{
    /// <summary>测试载荷：40 字节，分块 16 → 3 块（16/16/8）</summary>
    private static readonly byte[] Payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
    private const int ChunkSize = 16;
    private const int ChunkCount = 3;

    private readonly PostgresDatabaseFixture _fixture;
    private readonly string _stagingRoot;

    public UploadSessionResumeTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        _stagingRoot = Path.Combine(Path.GetTempPath(), "bm-upload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_stagingRoot, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    // ---------- 用例 ----------

    [Fact]
    public async Task 缺块查询_初始全部缺失()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);

        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        Assert.Equal("created", created.Status);
        Assert.Equal(ChunkSize, created.ChunkSizeBytes);
        var fileId = Assert.Single(created.Files).UploadFileId;

        var missing = await svc.GetMissingChunksAsync(clientId, created.UploadSessionId, fileId);
        Assert.Equal(Enumerable.Range(0, ChunkCount).ToList(), missing.Missing);
        Assert.NotNull(missing.Received);
        Assert.Empty(missing.Received);

        // 会话创建审计
        Assert.True(await db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == "upload.session.create" && a.ResourceId == created.UploadSessionId));
    }

    [Fact]
    public async Task 乱序上传_重复块_偏移校验()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);
        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        // 乱序：2 → 0 → 1
        foreach (var index in new[] { 2, 0, 1 })
        {
            var resp = await UploadChunkAsync(svc, clientId, sessionId, fileId, index);
            Assert.True(resp.Received);
        }

        var afterAll = await svc.GetMissingChunksAsync(clientId, sessionId, fileId);
        Assert.NotNull(afterAll.Missing);
        Assert.Empty(afterAll.Missing);
        Assert.Equal(Enumerable.Range(0, ChunkCount).ToList(), afterAll.Received);

        // 重复上传同一块：幂等 upsert，仍然只有一条分块记录
        var dup = await UploadChunkAsync(svc, clientId, sessionId, fileId, 1);
        Assert.True(dup.Received);
        Assert.Equal(1, await db.UploadChunks.AsNoTracking()
            .CountAsync(c => c.UploadFileId == fileId && c.ChunkIndex == 1));

        // 偏移与块序号不符 → CHUNK_INVALID
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.UploadChunkAsync(clientId, sessionId, fileId, 0, offset: 999,
                expectedHash: ChunkHash(0), data: ChunkStream(0)));
        Assert.Equal("CHUNK_INVALID", ex.ErrorCode);

        // 块序号越界 → CHUNK_INVALID
        var ex2 = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.UploadChunkAsync(clientId, sessionId, fileId, ChunkCount, offset: ChunkCount * ChunkSize,
                expectedHash: "00", data: new MemoryStream()));
        Assert.Equal("CHUNK_INVALID", ex2.ErrorCode);

        // 整文件哈希一致 → verified
        var complete = await svc.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        Assert.Equal("verified", complete.Status);
        Assert.Equal(FullHash(), complete.ServerSha256);
    }

    [Fact]
    public async Task 块哈希不匹配拒收()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);
        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        var wrongHash = new string('0', 64);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.UploadChunkAsync(clientId, sessionId, fileId, 0, offset: 0,
                expectedHash: wrongHash, data: ChunkStream(0)));
        Assert.Equal("CHUNK_HASH_MISMATCH", ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);

        // 文件被标记失败，且该块不计入已接收
        var file = await db.UploadFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
        Assert.Equal(UploadFileStatus.Failed, file.Status);
        Assert.Equal("CHUNK_HASH_MISMATCH", file.ErrorCode);

        var missing = await svc.GetMissingChunksAsync(clientId, sessionId, fileId);
        Assert.Contains(0, missing.Missing!);
    }

    [Fact]
    public async Task 整文件哈希比对()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);
        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        for (var i = 0; i < ChunkCount; i++)
            await UploadChunkAsync(svc, clientId, sessionId, fileId, i);

        // 错误整文件哈希 → FILE_HASH_MISMATCH
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.CompleteFileAsync(clientId, sessionId, fileId,
                new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = new string('f', 64) }));
        Assert.Equal("FILE_HASH_MISMATCH", ex.ErrorCode);

        var failed = await db.UploadFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
        Assert.Equal(UploadFileStatus.Failed, failed.Status);
        Assert.Equal("FILE_HASH_MISMATCH", failed.ErrorCode);
        Assert.Equal(FullHash(), failed.ServerSha256);   // 服务端真实哈希仍被记录

        // 分块未收齐时不得完成（另一文件语义：这里删除无必要，直接验证收齐后的成功路径）
        var ok = await svc.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        Assert.Equal("verified", ok.Status);

        // 已完成文件重复调用幂等返回
        var again = await svc.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        Assert.Equal("verified", again.Status);
    }

    /// <summary>
    /// 模拟进程重启：换一套 DI/DbContext（同一暂存根），凭幂等键找回原会话，
    /// 缺块查询反映已持久化的分块，只需补传缺失块即可完成。
    /// </summary>
    [Fact]
    public async Task 跨会话续传_幂等键恢复()
    {
        var idempotencyKey = $"it3-resume-{Guid.NewGuid():N}";
        Guid sessionId = Guid.Empty, fileId = Guid.Empty;
        var clientId = Guid.Empty;
        var candidateId = Guid.Empty;

        // 第一段"进程"：创建会话并只传 0、1 块
        await BuildServices().UsingAsync(async sp1 =>
        {
            await using var scope = sp1.CreateAsyncScope();
            var (svc, _, cId, candId) = await NewSessionContextAsync(scope);
            clientId = cId;
            candidateId = candId;

            var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId, idempotencyKey));
            Assert.Equal("created", created.Status);
            sessionId = created.UploadSessionId;
            fileId = created.Files[0].UploadFileId;

            await UploadChunkAsync(svc, clientId, sessionId, fileId, 0);
            await UploadChunkAsync(svc, clientId, sessionId, fileId, 1);
        });

        // 第二段"进程"：新的 DI 容器，凭幂等键恢复
        await using var sp2 = BuildServices();
        await using var scope2 = sp2.CreateAsyncScope();
        var svc2 = scope2.ServiceProvider.GetRequiredService<IUploadSessionService>();

        var resumed = await svc2.CreateSessionAsync(clientId, CreateRequest(candidateId, idempotencyKey));
        Assert.Equal("resumed", resumed.Status);
        Assert.Equal(sessionId, resumed.UploadSessionId);

        var missing = await svc2.GetMissingChunksAsync(clientId, sessionId, fileId);
        Assert.Equal(new List<int> { 2 }, missing.Missing);
        Assert.Equal(new List<int> { 0, 1 }, missing.Received);

        // 补传缺失块后完成
        await UploadChunkAsync(svc2, clientId, sessionId, fileId, 2);
        var complete = await svc2.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        Assert.Equal("verified", complete.Status);
    }

    [Fact]
    public async Task 大文件缺块查询返回范围()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IUploadSessionService>();

        var (clientId, candidateId) = await SeedAsync(db, fileSize: ChunkSize * 1100);

        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var fileId = created.Files[0].UploadFileId;

        var missing = await svc.GetMissingChunksAsync(clientId, created.UploadSessionId, fileId);
        Assert.Null(missing.Missing);                       // 超过 1024 块不再逐块返回
        var range = Assert.Single(missing.MissingRanges!);
        Assert.Equal(0, range.Start);
        Assert.Equal(1099, range.End);
    }

    // ---------- 基础设施 ----------

    /// <summary>组装与生产一致的 DI（真实暂存目录通过 system_settings.staging_path 注入）</summary>
    private ServiceProvider BuildServices()
    {
        // 先落库暂存根（每个测试实例独立的 SystemSettingsProvider，无旧缓存干扰）
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;
        using (var db = new AppDbContext(options))
        {
            var json = JsonSerializer.Serialize(_stagingRoot);
            db.Database.ExecuteSqlRaw(
                "UPDATE system_settings SET setting_value = CAST({0} AS jsonb) WHERE setting_key = 'staging_path'",
                json);
        }

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
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

    private record SessionContext(
        IUploadSessionService Service, AppDbContext Db, Guid ClientId, Guid CandidateId);

    /// <summary>种子一套客户端/任务/候选并返回服务上下文</summary>
    private async Task<SessionContext> NewSessionContextAsync(AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IUploadSessionService>();
        var (clientId, candidateId) = await SeedAsync(db, Payload.Length);
        return new SessionContext(svc, db, clientId, candidateId);
    }

    private static async Task<(Guid ClientId, Guid CandidateId)> SeedAsync(AppDbContext db, long fileSize)
    {
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"it3u-{suffix}",
            Hostname = $"it3u-host-{suffix[..8]}",
            DisplayName = $"上传测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"it3u-task-{suffix[..8]}",
            ApplicationName = "UploadTest",
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
            CandidateKey = $"it3u-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = fileSize,
            CreatedAt = now,
            UpdatedAt = now,
            Files =
            {
                new CandidateFile
                {
                    Id = Guid.NewGuid(),
                    RelativePath = "db.bak",
                    FileName = "db.bak",
                    SizeBytes = fileSize,
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

    private static CreateUploadSessionRequest CreateRequest(Guid candidateId, string? idempotencyKey = null)
        => new()
        {
            CandidateBackupSetId = candidateId,
            TotalFiles = 1,
            TotalBytes = Payload.Length,
            ChunkSizeBytes = ChunkSize,   // 服务层无 Range 校验（仅控制器层 DTO 注解），测试用小分块
            IdempotencyKey = idempotencyKey
        };

    private static Task<UploadChunkResponse> UploadChunkAsync(
        IUploadSessionService svc, Guid clientId, Guid sessionId, Guid fileId, int index)
        => svc.UploadChunkAsync(clientId, sessionId, fileId, index,
            offset: (long)index * ChunkSize, expectedHash: ChunkHash(index), data: ChunkStream(index));

    private static byte[] ChunkBytes(int index)
    {
        var start = index * ChunkSize;
        var length = Math.Min(ChunkSize, Payload.Length - start);
        return Payload.Skip(start).Take(length).ToArray();
    }

    private static MemoryStream ChunkStream(int index) => new(ChunkBytes(index));

    private static string ChunkHash(int index) => Hex(SHA256.HashData(ChunkBytes(index)));

    private static string FullHash() => Hex(SHA256.HashData(Payload));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>ServiceProvider 简易 using 扩展（模拟进程生命周期）</summary>
file static class ServiceProviderUsingExtensions
{
    public static async Task UsingAsync(this ServiceProvider provider, Func<ServiceProvider, Task> action)
    {
        await using var _ = provider;
        await action(provider);
    }
}
