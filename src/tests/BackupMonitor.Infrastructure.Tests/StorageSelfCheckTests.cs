using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 服务端磁盘自检。
///
/// 原来这段有两条静默路径：根目录解析抛异常就 LogWarning + return，
/// drive.IsReady == false 就 continue。结果是一个反过来的判定——
/// 「磁盘快满了」会报警，「磁盘整个不见了」反而一声不响，
/// 而后者比前者严重得多：备份此刻就落不了地。
///
/// 这两条用例钉的就是这两条路径，外加一条钉告警键不许与 server_storage_low 共用：
/// 共用会让两件事互相覆盖，而「加块盘」和「插上线」是完全不同的处置。
/// </summary>
[Collection("postgres")]
public class StorageSelfCheckTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;

    public StorageSelfCheckTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>路径被删 / 盘符不存在 / NAS 掉线：解析会抛，必须报 Critical 而不是默默跳过。</summary>
    [Fact]
    public async Task 存储根目录解析失败时报不可达告警()
    {
        await using var services = BuildServices(
            new StubUploadStorage
            {
                RepositoryRoot = () => throw new DirectoryNotFoundException("盘符 Q:\\ 不存在"),
                StagingRoot = () => Path.GetTempPath()
            });

        await RunStorageSelfCheckAsync(services);

        var alert = await RepositoryAlertAsync(services, "server_storage_unavailable");
        Assert.Equal(AlertLevel.Critical, alert.Level);
        Assert.Contains("仓库", alert.Title);
    }

    /// <summary>外挂盘掉电 / 卷未挂载：drive.IsReady == false，原来是 continue。</summary>
    [Fact]
    public async Task 盘未就绪时报不可达告警()
    {
        // 挑一个本机没有挂载的盘符——DriveInfo 对未挂载盘不抛异常，
        // 只是 IsReady == false，正是要覆盖的那条分支。
        var missing = FindUnmountedDriveLetter();
        Assert.True(missing is not null, "本机 D-Z 盘符已全部挂载，无法构造未就绪盘");

        await using var services = BuildServices(
            new StubUploadStorage
            {
                RepositoryRoot = () => $"{missing}\\repository",
                StagingRoot = () => Path.GetTempPath()
            });

        await RunStorageSelfCheckAsync(services);

        var alert = await RepositoryAlertAsync(services, "server_storage_unavailable");
        Assert.Equal(AlertLevel.Critical, alert.Level);
        Assert.EndsWith(":unavailable", alert.AlertKey);
    }

    /// <summary>「盘没了」与「盘快满了」必须是两个键，否则互相覆盖。</summary>
    [Fact]
    public async Task 不可达告警键与空间不足告警键不相同()
    {
        await using var services = BuildServices(
            new StubUploadStorage
            {
                RepositoryRoot = () => throw new UnauthorizedAccessException("权限丢失"),
                StagingRoot = () => Path.GetTempPath()
            });

        await RunStorageSelfCheckAsync(services);

        var alert = await RepositoryAlertAsync(services, "server_storage_unavailable");
        Assert.NotEqual("system:storage:仓库", alert.AlertKey);
    }

    // ---------- 基础设施 ----------

    private static string? FindUnmountedDriveLetter()
    {
        var mounted = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        // 从 Z 往回找，避开 A/B 软驱盘符（有些环境下它们的行为不一致）。
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!mounted.Contains(letter))
                return $"{letter}:";
        }
        return null;
    }

    private ServiceProvider BuildServices(IUploadStorage storage)
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddSingleton(storage);
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        return sc.BuildServiceProvider();
    }

    private static async Task RunStorageSelfCheckAsync(ServiceProvider services)
    {
        var worker = new SystemWatchdogWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SystemWatchdogWorker>.Instance);

        using var scope = services.CreateScope();
        await worker.RunStorageSelfCheckAsync(scope, CancellationToken.None);
    }

    /// <summary>
    /// 只取「仓库」这一个根的告警。整个 postgres 测试集合共用一个库，
    /// 断言「全表只有一条」会被同集合里其它用例的残留污染，钉的东西也不对——
    /// 要钉的是「这个根报出了不可达」，不是「全库只有这一条告警」。
    /// </summary>
    private static async Task<(string AlertKey, AlertLevel Level, string Title)> RepositoryAlertAsync(
        ServiceProvider services,
        string category)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Alerts.AsNoTracking()
            .Where(a => a.Category == category && a.AlertKey.Contains("仓库"))
            .Select(a => new { a.AlertKey, a.Level, a.Title })
            .SingleAsync();
        return (row.AlertKey, row.Level, row.Title);
    }

    /// <summary>
    /// 只有两个根解析方法有意义，其余成员这条链路不会走到；
    /// 走到了就是实现改了口径，让它当场抛比悄悄返回默认值好。
    /// </summary>
    private sealed class StubUploadStorage : IUploadStorage
    {
        public required Func<string> RepositoryRoot { get; init; }
        public required Func<string> StagingRoot { get; init; }

        public Task<string> GetRepositoryRootAsync(CancellationToken ct = default) =>
            Task.FromResult(RepositoryRoot());

        public Task<string> GetStagingRootAsync(CancellationToken ct = default) =>
            Task.FromResult(StagingRoot());

        public Task<StorageRootResolution> ResolveRootAsync(string settingKey, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> GetSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> EnsureSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> GetStagingFreeBytesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<(string Hash, long BytesWritten)> WriteChunkAsync(
            Guid sessionId, Guid uploadFileId, int chunkIndex, long offset, Stream data, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> ComputeFileHashAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<(bool Exists, long Length)> GetStagedFileInfoAsync(
            Guid sessionId, Guid uploadFileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenStagedFileAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> GetStagedFilePathAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CleanupSessionAsync(Guid sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
