using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Upload;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 管理端传输进度查询。
///
/// upload_sessions 里每收一个分块就更新一次 uploaded_bytes 和 last_activity_at，
/// 但这些数字此前只通过 Agent 端接口回给客户端自己（断点续传要用）。
/// 管理端能看到的只有一个「上传中」徽标——传一份几十 GB 的备份，
/// 界面上从头到尾没有一个数字在动，人分不出「在传」和「卡死了」。
/// </summary>
[Collection("postgres")]
public class UploadProgressQueryTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public UploadProgressQueryTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<UploadRateSampler>();
        sc.AddScoped<IUploadProgressService, UploadProgressService>();
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task 只返回还在传的会话()
    {
        var mine = await SeedAsync(UploadStatus.Uploading, totalBytes: 1000, uploadedBytes: 500);
        var committed = await SeedAsync(UploadStatus.Committed, totalBytes: 1000, uploadedBytes: 1000);
        var cancelled = await SeedAsync(UploadStatus.Cancelled, totalBytes: 1000, uploadedBytes: 300);

        var rows = await QueryAsync();

        Assert.Contains(rows, r => r.SessionId == mine);
        // 已入库和已取消的会话留在这里只会让人以为备份还在跑。
        Assert.DoesNotContain(rows, r => r.SessionId == committed);
        Assert.DoesNotContain(rows, r => r.SessionId == cancelled);
    }

    [Fact]
    public async Task 百分比按已传字节算()
    {
        var id = await SeedAsync(UploadStatus.Uploading, totalBytes: 4000, uploadedBytes: 1000);

        var row = Assert.Single(await QueryAsync(), r => r.SessionId == id);

        Assert.Equal(25m, row.Percent);
        Assert.Equal(1000, row.UploadedBytes);
        Assert.Equal(4000, row.TotalBytes);
    }

    [Fact]
    public async Task 总量为零时不除零()
    {
        // 空备份集是合法的（源目录这一轮没有新文件），
        // 除零会让整个接口 500，把其它正在传的会话一起挡在外面。
        var id = await SeedAsync(UploadStatus.Uploading, totalBytes: 0, uploadedBytes: 0);

        var row = Assert.Single(await QueryAsync(), r => r.SessionId == id);

        Assert.Equal(0m, row.Percent);
    }

    [Fact]
    public async Task 速度未知时不给剩余时间()
    {
        // 会话刚建出来，一个字节都还没传，算不出速度。
        // 这时给「剩余 0 秒」比不给更糟——它看起来像马上就好了。
        var id = await SeedAsync(UploadStatus.Created, totalBytes: 4000, uploadedBytes: 0);

        var row = Assert.Single(await QueryAsync(), r => r.SessionId == id);

        Assert.Null(row.BytesPerSecond);
        Assert.Null(row.EtaSeconds);
    }

    [Fact]
    public async Task 首次查询用全程平均给出速度和剩余时间()
    {
        // 采样器第一次见到这个会话，没有可比的上一次，此时退回全程平均，
        // 而不是让界面上的速度那一列一直空着。
        var id = await SeedAsync(UploadStatus.Uploading, totalBytes: 3000, uploadedBytes: 1000,
            startedAgo: TimeSpan.FromSeconds(100));

        var row = Assert.Single(await QueryAsync(), r => r.SessionId == id);

        // 1000 字节 / 100 秒；余下 2000 字节按这个速度还要 200 秒。
        // 播种到查询之间的耗时会让分母略大于 100，因此给一格容差而不是死等 10。
        Assert.InRange(row.BytesPerSecond ?? 0, 9, 10);
        Assert.InRange(row.EtaSeconds ?? 0, 200, 222);
    }

    [Fact]
    public async Task 长时间没有新数据的会话被标为疑似卡住()
    {
        // 全程平均 1 MB/s，按这个速度传一个分块远不到下限阈值，
        // 因此阈值取 180 秒，而它已经一小时没收到任何数据了。
        var id = await SeedAsync(UploadStatus.Uploading,
            totalBytes: 100L * 1024 * 1024 * 1024,
            uploadedBytes: 3600L * 1024 * 1024,
            startedAgo: TimeSpan.FromSeconds(3600),
            idle: TimeSpan.FromSeconds(3600));

        var row = Assert.Single(await QueryAsync(), r => r.SessionId == id);

        Assert.True(row.Stalled, "一小时没有新数据必须标出来，否则人会一直等下去");
        Assert.InRange(row.IdleSeconds, 3590, 3700);
    }

    [Fact]
    public async Task 刚收到数据的会话不被标为卡住()
    {
        var id = await SeedAsync(UploadStatus.Uploading, totalBytes: 4000, uploadedBytes: 1000,
            startedAgo: TimeSpan.FromSeconds(100), idle: TimeSpan.Zero);

        var row = Assert.Single(await QueryAsync(), r => r.SessionId == id);

        Assert.False(row.Stalled);
    }

    [Fact]
    public async Task 卡住的排在最前面()
    {
        // 排序口径：先卡住的，再剩余量大的。
        // 卡住那条需要人动手，剩余量最大那条决定整批什么时候结束——
        // 按创建时间排会让这两条都沉到列表中间。
        var big = await SeedAsync(UploadStatus.Uploading,
            totalBytes: 900L * 1024 * 1024 * 1024, uploadedBytes: 0, idle: TimeSpan.Zero);
        var stalled = await SeedAsync(UploadStatus.Uploading,
            totalBytes: 1024, uploadedBytes: 512,
            startedAgo: TimeSpan.FromSeconds(3600), idle: TimeSpan.FromSeconds(3600));

        var rows = (await QueryAsync())
            .Where(r => r.SessionId == big || r.SessionId == stalled)
            .ToList();

        Assert.Equal(stalled, rows[0].SessionId);
        Assert.Equal(big, rows[1].SessionId);
    }

    // ---------- 基础设施 ----------

    private async Task<IReadOnlyList<UploadProgressDto>> QueryAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUploadProgressService>().GetActiveAsync();
    }

    private async Task<Guid> SeedAsync(
        UploadStatus status,
        long totalBytes,
        long uploadedBytes,
        TimeSpan? startedAgo = null,
        TimeSpan? idle = null)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"prog-{suffix}",
            Hostname = $"prog-host-{suffix[..8]}",
            DisplayName = $"进度测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"prog-task-{suffix[..8]}",
            ApplicationName = "ProgressTest",
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
            CandidateKey = $"prog-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = Math.Max(1, totalBytes),
            CreatedAt = now,
            UpdatedAt = now
        };
        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            TaskId = task.Id,
            CandidateBackupSetId = candidate.Id,
            Status = status,
            TotalFiles = 1,
            TotalBytes = totalBytes,
            UploadedBytes = uploadedBytes,
            ChunkSizeBytes = 8 * 1024 * 1024,
            StartedAt = startedAgo is null ? null : now - startedAgo.Value,
            LastActivityAt = idle is null ? null : now - idle.Value,
            CreatedAt = now - (startedAgo ?? TimeSpan.Zero),
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        db.CandidateBackupSets.Add(candidate);
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }
}
