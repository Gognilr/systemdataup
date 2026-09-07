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

        // 分块收齐 → received。
        // 实施方案 T2：整文件哈希复核挪到入库阶段（UploadCommitWorker），
        // complete-file 只做常数时间的检查，因此这里不再是 verified、
        // 也不再回 ServerSha256——那两样都要等入库复核之后才有。
        var complete = await svc.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        Assert.Equal("received", complete.Status);
        Assert.Null(complete.ServerSha256);

        // 客户端声明的哈希被存下来，留给入库复核做交叉验证——
        // 原有的「双向验证」语义一个字都不能少，只是比对的时点后移了。
        var stored = await db.UploadFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
        Assert.Equal(FullHash(), stored.ClientDeclaredSha256);
    }

    [Fact]
    public async Task 重传同一块不会把已传字节算两遍()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);
        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        await UploadChunkAsync(svc, clientId, sessionId, fileId, 0);
        await UploadChunkAsync(svc, clientId, sessionId, fileId, 0);
        await UploadChunkAsync(svc, clientId, sessionId, fileId, 0);

        // 计数从「每块重新 SUM 一遍」改成了数据库侧增量累加，
        //「这一块是新收的还是重传的」由 upsert 的 xmax 判定。判错的表现是
        // 进度超过 100%、剩余时间变成负数——重试在弱网下是常态，必须钉住。
        var file = await db.UploadFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
        var session = await db.UploadSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(1, file.UploadedChunks);
        Assert.Equal(ChunkSize, file.UploadedBytes);
        Assert.Equal(ChunkSize, session.UploadedBytes);
    }

    [Fact]
    public async Task 并发上传多块时计数不丢且整文件哈希仍然正确()
    {
        await using var sp = BuildServices();
        await using var setup = sp.CreateAsyncScope();
        var (svc, _, clientId, candidateId) = await NewSessionContextAsync(setup);
        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        // 全部分块同时上传。每路各用一个 scope：AppDbContext 不是线程安全的，
        // 而真实场景里这几个请求本来就落在各自独立的请求作用域上。
        // 客户端现在默认并行 4 路发送，这条用例复现的就是那个形状。
        await Task.WhenAll(Enumerable.Range(0, ChunkCount).Select(async index =>
        {
            await using var parallelScope = sp.CreateAsyncScope();
            var parallelSvc = parallelScope.ServiceProvider.GetRequiredService<IUploadSessionService>();
            await UploadChunkAsync(parallelSvc, clientId, sessionId, fileId, index);
        }));

        // 增量累加发生在数据库里（uploaded_bytes = uploaded_bytes + n），
        // 不存在应用层「读-改-写」的丢更新。这条断言钉住的就是这一点。
        await using var verifyScope = sp.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await verifyDb.UploadFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
        var session = await verifyDb.UploadSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(ChunkCount, file.UploadedChunks);
        Assert.Equal(Payload.Length, file.UploadedBytes);
        Assert.Equal(Payload.Length, session.UploadedBytes);

        // 几块并发写进同一个暂存文件，整文件哈希必须仍然对得上——
        // 各块写入区间由 offset 严格划分、互不重叠，这是允许并发写的前提。
        var complete = await svc.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        Assert.Equal("received", complete.Status);
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

    /// <summary>
    /// complete-file 的判定范围（实施方案 T2 之后）。
    ///
    /// 整文件哈希复核挪去了入库阶段，这里只保留常数时间的检查。
    /// 这条测试因此改成验「哈希对不对**不再**在这一步决定，但长度对不对仍然在」——
    /// 后者是能在最早时点拦住「块都收齐了、暂存文件长度却不对」这种自相矛盾状态的那一道。
    /// 哈希复核本身由 UploadCommitHashVerificationTests 验。
    /// </summary>
    [Fact]
    public async Task 完成文件只做常数时间检查()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);
        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        for (var i = 0; i < ChunkCount; i++)
            await UploadChunkAsync(svc, clientId, sessionId, fileId, i);

        // 客户端声明一个错的哈希，complete-file 不再据此判定——它被存下来，
        // 等入库复核时和服务端实测值一起比。这一步不再整读文件，因此也不可能在这里发现它错。
        var wrong = await svc.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = new string('f', 64) });
        Assert.Equal("received", wrong.Status);

        var stored = await db.UploadFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
        Assert.Equal(UploadFileStatus.Received, stored.Status);
        Assert.Equal(new string('f', 64), stored.ClientDeclaredSha256);
        Assert.Null(stored.ServerSha256);   // 服务端实测值要等入库复核才有

        // 已完成文件重复调用幂等返回
        var again = await svc.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        Assert.Equal("received", again.Status);
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
        Assert.Equal("received", complete.Status);
    }

    /// <summary>
    /// 待办方案 E：暂停必须真的停住写入。
    /// paused 此前被列在可写状态里，于是这个状态只是个标签——
    /// 界面上点了暂停，客户端照传不误。
    /// </summary>
    [Fact]
    public async Task 暂停中的会话拒绝写入()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);

        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        await UploadChunkAsync(svc, clientId, sessionId, fileId, 0);

        await db.UploadSessions.Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, UploadStatus.Paused));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => UploadChunkAsync(svc, clientId, sessionId, fileId, 1));
        Assert.Equal(409, ex.StatusCode);

        // 错误码要能和「会话状态异常」区分开：Agent 靠它决定是安静停下还是重试。
        // 共用一个泛化的 UPLOAD_SESSION_CONFLICT 时，Agent 只能一律当成可重试错误，
        // 于是暂停变成「退避几秒后接着传」。
        Assert.Equal(UploadSessionService.PausedErrorCode, ex.ErrorCode);

        // 已经传上来的块一块都不能少：暂停不是取消
        var missing = await svc.GetMissingChunksAsync(clientId, sessionId, fileId);
        Assert.Equal(new List<int> { 0 }, missing.Received);
    }

    /// <summary>
    /// **点了暂停几秒后传输自己又跑起来**的那个缺陷。
    ///
    /// 根因不在守卫，而在记录分块的那条裸 SQL：它无条件写 `status = 'uploading'`。
    /// 管理端把会话置 paused 之后，任何一个已经通过守卫、还在路上的分块落库都会把状态
    /// 改回去——而 Agent 默认 4 路并行、每块 8MB，点暂停的那一刻几乎必然有在途块。
    ///
    /// 所以这里直接调落库那一步，绕过守卫：走公开接口的话写入在守卫处就被挡下，
    /// 根本到不了出问题的那条 UPDATE，测了也测不到东西。
    ///
    /// 两个断言缺一不可：状态不许被改回去，**而已传字节必须照常累加**——
    /// 那一块确实收到了、确实落在暂存目录里，进度倒退会让恢复后的 missing-chunks
    /// 与 uploaded_bytes 对不上。
    /// </summary>
    [Fact]
    public async Task 暂停中落库的在途分块不会把状态刷回传输中()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);

        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        await UploadChunkAsync(svc, clientId, sessionId, fileId, 0);
        await db.UploadSessions.Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, UploadStatus.Paused));

        var before = await db.UploadSessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.UploadedBytes).FirstAsync();

        // 模拟一个在守卫之后、暂停之前就已经上路的分块，现在才落库。
        var concrete = (UploadSessionService)svc;
        var bytes = ChunkBytes(1);
        await concrete.RecordReceivedChunkAsync(
            sessionId, fileId, chunkIndex: 1, offset: ChunkSize, chunkBytes: bytes.Length,
            expectedHash: ChunkHash(1), serverHash: ChunkHash(1), CancellationToken.None);

        var after = await db.UploadSessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.Status, s.UploadedBytes })
            .FirstAsync();

        Assert.Equal(UploadStatus.Paused, after.Status);
        Assert.Equal(before + bytes.Length, after.UploadedBytes);
    }

    /// <summary>
    /// 暂停必须挡住**所有**向前推进的路径，不只是分块写入那一条。
    ///
    /// 分块收齐之后暂停的话，完成文件、完成会话这两条路原先都是畅通的——
    /// 会话会一路走到 verifying 再入库，暂停等于没按。
    /// </summary>
    [Fact]
    public async Task 暂停中不能完成文件也不能完成会话()
    {
        await using var sp = BuildServices();
        Guid clientId = Guid.Empty, candidateId = Guid.Empty, sessionId = Guid.Empty, fileId = Guid.Empty;

        // 第一段「请求」：把三块都传上去，然后管理端把会话暂停。
        await using (var scope = sp.CreateAsyncScope())
        {
            var (svc, db, cId, candId) = await NewSessionContextAsync(scope);
            clientId = cId;
            candidateId = candId;

            var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId));
            sessionId = created.UploadSessionId;
            fileId = created.Files[0].UploadFileId;

            for (var i = 0; i < ChunkCount; i++)
                await UploadChunkAsync(svc, clientId, sessionId, fileId, i);

            await db.UploadSessions.Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, UploadStatus.Paused));
        }

        // 第二段「请求」：新作用域、新 DbContext——与线上一致。
        // 同一个作用域里做不了这个断言：会话实体还在变更跟踪里，
        // ExecuteUpdate 绕过了跟踪器，再查回来拿到的是缓存里那份旧状态。
        await using var next = sp.CreateAsyncScope();
        var service = next.ServiceProvider.GetRequiredService<IUploadSessionService>();
        var database = next.ServiceProvider.GetRequiredService<AppDbContext>();

        var fileEx = await Assert.ThrowsAsync<BusinessException>(() =>
            service.CompleteFileAsync(clientId, sessionId, fileId,
                new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() }));
        Assert.Equal(UploadSessionService.PausedErrorCode, fileEx.ErrorCode);

        var sessionEx = await Assert.ThrowsAsync<BusinessException>(() =>
            service.CompleteSessionAsync(clientId, sessionId, new CompleteUploadSessionRequest
            {
                TotalFiles = 1,
                TotalBytes = Payload.Length
            }));
        Assert.Equal(UploadSessionService.PausedErrorCode, sessionEx.ErrorCode);

        var status = await database.UploadSessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.Status).FirstAsync();
        Assert.Equal(UploadStatus.Paused, status);
    }

    /// <summary>
    /// 幂等键命中一个暂停中的会话时不能把它交回给 Agent。
    ///
    /// 自动模式下预检每跑一轮就会用 auto-upload:{candidateId} 重下一条上传指令，
    /// 幂等键命中的正是被暂停的那个会话——照常返回「可续传」等于绕过管理员的暂停。
    /// </summary>
    [Fact]
    public async Task 暂停中的会话不会被幂等键重新交给客户端()
    {
        await using var sp = BuildServices();
        var key = $"auto-upload:{Guid.NewGuid():N}";
        Guid clientId = Guid.Empty, candidateId = Guid.Empty;

        await using (var scope = sp.CreateAsyncScope())
        {
            var (svc, db, cId, candId) = await NewSessionContextAsync(scope);
            clientId = cId;
            candidateId = candId;

            var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId, key));
            await db.UploadSessions.Where(s => s.Id == created.UploadSessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, UploadStatus.Paused));
        }

        // 新作用域＝下一条上传指令。理由同上一个用例。
        await using var next = sp.CreateAsyncScope();
        var service = next.ServiceProvider.GetRequiredService<IUploadSessionService>();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.CreateSessionAsync(clientId, CreateRequest(candidateId, key)));

        Assert.Equal(UploadSessionService.PausedErrorCode, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
    }

    /// <summary>
    /// 待办方案 E 的另一半：出错也要能续传。
    /// Agent 原先遇到异常一律调 cancel，会话作废、幂等键释放，下一次从 0 开始——
    /// 「断网能续传，出错不能续传」。中断则把会话停在可续传的状态。
    /// </summary>
    [Fact]
    public async Task 出错中断后接着传而不是从头传()
    {
        var idempotencyKey = $"it3-interrupt-{Guid.NewGuid():N}";

        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var (svc, db, clientId, candidateId) = await NewSessionContextAsync(scope);

        var created = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId, idempotencyKey));
        var sessionId = created.UploadSessionId;
        var fileId = created.Files[0].UploadFileId;

        await UploadChunkAsync(svc, clientId, sessionId, fileId, 0);
        await UploadChunkAsync(svc, clientId, sessionId, fileId, 1);

        // 传到一半出错
        await svc.InterruptSessionAsync(clientId, sessionId,
            new InterruptUploadSessionRequest { ErrorCode = "UPLOAD_ERROR", ErrorMessage = "连接被重置" });

        var session = await db.UploadSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(UploadStatus.RetryWait, session.Status);
        Assert.Equal("UPLOAD_ERROR", session.ErrorCode);

        // 下一条上传指令凭幂等键拿回同一个会话，只补缺的那一块
        var resumed = await svc.CreateSessionAsync(clientId, CreateRequest(candidateId, idempotencyKey));
        Assert.Equal("resumed", resumed.Status);
        Assert.Equal(sessionId, resumed.UploadSessionId);

        var missing = await svc.GetMissingChunksAsync(clientId, sessionId, fileId);
        Assert.Equal(new List<int> { 2 }, missing.Missing);

        await UploadChunkAsync(svc, clientId, sessionId, fileId, 2);
        var complete = await svc.CompleteFileAsync(clientId, sessionId, fileId,
            new CompleteUploadFileRequest { SizeBytes = Payload.Length, Sha256 = FullHash() });
        Assert.Equal("received", complete.Status);
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
