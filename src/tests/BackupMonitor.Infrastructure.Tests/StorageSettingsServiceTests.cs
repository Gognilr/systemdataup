using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Upload;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 备份存放目录 / 上传暂存目录的读写。
///
/// 这两个路径以前只在服务端安装向导里问一次，装完既没有界面能看当前值，也没有界面能改——
/// "我的备份到底存到哪去了"要翻 system_settings 表才有答案。补入口时真正要钉住的是三件事：
/// 没配置时的回退链不能炸、保存后必须立刻生效（不是 60 秒后）、以及写不进去的路径必须当场
/// 拒绝而不是等下一次真备份时失败。
/// </summary>
[Collection("postgres")]
public class StorageSettingsServiceTests : IDisposable
{
    private readonly PostgresDatabaseFixture _fixture;
    private readonly string _tempRoot;

    public StorageSettingsServiceTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
        _tempRoot = Path.Combine(Path.GetTempPath(), "bm-storage-settings-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// postgres 集合共用一个库，用例之间会互相看见对方写的 system_settings。
    /// 每个用例结束后把两个键清回 null，免得后面的用例读到本用例的临时目录。
    /// </summary>
    public void Dispose()
    {
        ResetSettings();
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* 临时目录清理失败不影响断言 */ }
        GC.SuppressFinalize(this);
    }

    private void ResetSettings()
    {
        using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            "UPDATE system_settings SET setting_value = 'null'::jsonb WHERE setting_key IN ('repository_path', 'staging_path');"
            // 并发上限的用例会把它改掉，收尾时放回 V030 的出厂值。
            + "UPDATE system_settings SET setting_value = '4'::jsonb WHERE setting_key = 'max_concurrent_uploads_total'",
            connection);
        command.ExecuteNonQuery();

        // 换根的守卫数的是全库还没结束的上传会话，而 postgres 集合里的各个测试类共用一个库，
        // 前面的类留下的在传会话会让这里所有改路径的用例撞上 409。终结掉即可——
        // 同集合内各类是顺序跑的，跑完的类不会再回头看这些行。不能 DELETE：
        // 别人留下的会话下面可能还挂着分块，删父行会直接撞外键。
        using var terminate = new NpgsqlCommand(
            "UPDATE upload_sessions SET status = 'expired' WHERE status IN "
            + "('created', 'waiting_permission', 'uploading', 'paused', 'retry_wait', 'received', 'verifying')",
            connection);
        terminate.ExecuteNonQuery();
    }

    /// <summary>
    /// 生产配置形态：appsettings.json 里 Storage:RepositoryPath / StagingPath 的出厂值是空串
    /// （不是缺省键）。用 InMemoryCollection 原样复现，否则测出来的是一个现实中不存在的配置。
    /// </summary>
    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton<ICurrentContext, NullCurrentContext>();
        services.AddScoped<IAuditRecorder, DbAuditRecorder>();
        services.AddSingleton(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:RepositoryPath"] = "",
            ["Storage:StagingPath"] = ""
        }).Build() as IConfiguration);
        services.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SystemSettingsProvider>.Instance));
        services.AddScoped<IUploadStorage, UploadStorage>();
        services.AddScoped<IStorageSettingsService, StorageSettingsService>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 库里没配路径时（V014 之后所有非一键安装的部署都是这个状态），回退链必须走到
    /// 程序目录下的 data\repository，而不是被 appsettings 里的空串接管。
    ///
    /// 原实现写的是 `_configuration["Storage:RepositoryPath"] ?? 默认值`——空串不是 null，
    /// 于是 root 变成 ""，Directory.CreateDirectory("") 抛 ArgumentException。
    /// 表现是概览页容量统计整页 500，而报错信息跟"路径"两个字毫无关系。
    /// </summary>
    [Fact]
    public async Task 未配置路径时_回退到程序目录默认值而不是配置文件里的空串()
    {
        ResetSettings();
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var settings = await service.GetAsync();

        Assert.Equal("default", settings.Repository.Source);
        Assert.Equal("default", settings.Staging.Source);
        Assert.Null(settings.Repository.ConfiguredPath);
        Assert.EndsWith(Path.Combine("data", "repository"), settings.Repository.EffectivePath);
        Assert.EndsWith(Path.Combine("data", "staging"), settings.Staging.EffectivePath);

        // 同一条回退链在真正解析仓库根时也必须走得通（这里才是原先抛异常的地方）
        var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();
        var root = await storage.GetRepositoryRootAsync();
        Assert.True(Directory.Exists(root));
    }

    /// <summary>
    /// 保存后必须立刻生效。SystemSettingsProvider 有 60 秒缓存，不作废的话，
    /// 管理员点完保存、页面刷新，看到的仍然是旧路径——分不清是没保存成功还是缓存。
    /// </summary>
    [Fact]
    public async Task 保存路径后_立刻生效且不受配置缓存影响()
    {
        ResetSettings();
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();
        var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();

        // 先读一次，把"未配置"的结果灌进缓存
        await service.GetAsync();

        var repository = Path.Combine(_tempRoot, "repository");
        var staging = Path.Combine(_tempRoot, "staging");
        var updated = await service.UpdateAsync(new UpdateStorageSettingsRequest
        {
            RepositoryPath = repository,
            StagingPath = staging
        });

        Assert.Equal("database", updated.Repository.Source);
        Assert.Equal(repository, updated.Repository.EffectivePath);
        Assert.True(updated.Repository.Exists);
        Assert.True(updated.Repository.Writable);
        Assert.Null(updated.Repository.Problem);

        Assert.Equal(repository, await storage.GetRepositoryRootAsync());
        Assert.Equal(staging, await storage.GetStagingRootAsync());
    }

    /// <summary>留空表示"不指定"，应当清除配置并回落默认值，而不是把空串存进库。</summary>
    [Fact]
    public async Task 留空提交_清除配置并回落默认值()
    {
        ResetSettings();
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        await service.UpdateAsync(new UpdateStorageSettingsRequest
        {
            RepositoryPath = Path.Combine(_tempRoot, "repository"),
            StagingPath = Path.Combine(_tempRoot, "staging")
        });

        var cleared = await service.UpdateAsync(new UpdateStorageSettingsRequest
        {
            RepositoryPath = "   ",
            StagingPath = null
        });

        Assert.Equal("default", cleared.Repository.Source);
        Assert.Equal("default", cleared.Staging.Source);
        Assert.Null(cleared.Repository.ConfiguredPath);
        Assert.Null(cleared.Staging.ConfiguredPath);
    }

    /// <summary>
    /// 相对路径按服务进程的当前目录解释，装成 Windows 服务之后那是 system32——
    /// 备份会落在一个谁也想不到的地方。必须在保存时就拒绝。
    /// </summary>
    [Fact]
    public async Task 相对路径被拒绝()
    {
        ResetSettings();
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.UpdateAsync(
            new UpdateStorageSettingsRequest { RepositoryPath = @"backup\repository" }));
    }

    /// <summary>
    /// 驱动器根会让备份直接铺在整个磁盘根目录上，而保留策略删除时的"至少两级目录"守卫
    /// （RetentionCleanupWorker.DeleteRepositoryDirectory）会拒绝清理这类路径——
    /// 建出来就是个永远删不掉的仓库。
    /// </summary>
    [Fact]
    public async Task 驱动器根目录被拒绝()
    {
        ResetSettings();
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var driveRoot = Path.GetPathRoot(Path.GetFullPath(_tempRoot))!;
        await Assert.ThrowsAsync<ValidationFailedException>(() => service.UpdateAsync(
            new UpdateStorageSettingsRequest { RepositoryPath = driveRoot }));
    }

    /// <summary>
    /// UNC 共享根同样要拒绝（保留策略的"至少两级目录"守卫对它一样删不掉），
    /// 而且提示里给出的示例路径必须是能照抄的。
    ///
    /// 盘符根自带尾部反斜杠、UNC 共享根不带，早先用字符串相加拼示例，
    /// 于是 UNC 情况下给出的是 \\nas\backupBackupRepository —— 照着改一遍还是错的。
    /// </summary>
    [Fact]
    public async Task UNC共享根被拒绝_且示例路径带分隔符()
    {
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => service.UpdateAsync(
            new UpdateStorageSettingsRequest { RepositoryPath = @"\\nas\backup" }));

        Assert.Contains(@"\\nas\backup\BackupRepository", ex.Message);
    }

    /// <summary>
    /// 管理页面多半不是在服务器本机上打开的。响应必须带上服务端主机名，
    /// 界面才能说清楚"这些路径和磁盘是哪台机器上的"——否则很容易被当成本机路径。
    /// </summary>
    [Fact]
    public async Task 读取设置_带回服务端主机名()
    {
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var settings = await service.GetAsync();

        Assert.Equal(Environment.MachineName, settings.ServerHostname);
    }

    /// <summary>
    /// 目录浏览只返回子目录，不返回文件。
    ///
    /// 这个接口的存在意义就是"别让人手敲路径"——敲错一个字符不会报错，备份会安静地
    /// 写进另一个目录，等到要恢复时才发现。同时它绝不能变成一个文件浏览器：
    /// 选存储根用不到文件名，多列一样东西就多一分把服务器目录内容泄露出去的面。
    /// </summary>
    [Fact]
    public async Task 浏览目录_只列子目录不列文件()
    {
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        Directory.CreateDirectory(Path.Combine(_tempRoot, "repository"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "staging"));
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "readme.txt"), "不该出现在浏览结果里");

        var result = await service.BrowseAsync(_tempRoot);

        Assert.False(result.IsDriveList);
        Assert.Equal(Path.GetFullPath(_tempRoot), result.Path);
        Assert.NotNull(result.ParentPath);
        Assert.Equal(new[] { "repository", "staging" }, result.Entries.Select(e => e.Name).ToArray());
        Assert.DoesNotContain(result.Entries, e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>不传路径时返回磁盘列表，并带上容量——选盘时最需要知道的就是还剩多少空间。</summary>
    [Fact]
    public async Task 不传路径_返回带容量的磁盘列表()
    {
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var result = await service.BrowseAsync(null);

        Assert.True(result.IsDriveList);
        Assert.NotEmpty(result.Entries);
        Assert.All(result.Entries, e =>
        {
            Assert.True(e.IsDrive);
            Assert.NotNull(e.FreeBytes);
            Assert.NotNull(e.TotalBytes);
        });
    }

    /// <summary>浏览也必须挡住相对路径，否则它会按服务进程的当前目录解释。</summary>
    [Fact]
    public async Task 浏览相对路径被拒绝()
    {
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.BrowseAsync(@"windows\system32"));
    }

    /// <summary>浏览不存在的目录返回 404，而不是抛一个未处理异常把整页打成 500。</summary>
    [Fact]
    public async Task 浏览不存在的目录_返回404()
    {
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var missing = Path.Combine(_tempRoot, "no-such-directory");
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.BrowseAsync(missing));
        Assert.Equal(404, ex.StatusCode);
    }

    /// <summary>正式仓库和上传暂存指向同一个目录：半成品和正式备份会混在一起。</summary>
    [Fact]
    public async Task 仓库与暂存指向同一目录时被拒绝()
    {
        ResetSettings();
        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var same = Path.Combine(_tempRoot, "shared");
        await Assert.ThrowsAsync<ValidationFailedException>(() => service.UpdateAsync(
            new UpdateStorageSettingsRequest { RepositoryPath = same, StagingPath = same }));
    }

    /// <summary>
    /// 有传输没结束时不许换根。
    ///
    /// 在传的会话把已收到的分块留在旧暂存根下，续传是按「暂存根 + 会话 ID」找回去的；
    /// verifying 的那几条正被提交工作器从暂存往旧仓库根里搬。换根之后前者的断点在服务端
    /// 已经不存在，后者的备份分在两个目录里——两种都是"界面写着成功、文件不在该在的地方"。
    /// </summary>
    [Fact]
    public async Task 有传输没结束时_拒绝更换存储根()
    {
        ResetSettings();
        await using var provider = BuildServices();
        await SeedInFlightUploadAsync(provider, UploadStatus.Uploading);

        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(
            new UpdateStorageSettingsRequest { RepositoryPath = Path.Combine(_tempRoot, "repository") }));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("传输", ex.Message);

        // 拦下来就必须什么都没写：留下"仓库改了、暂存没改"才是最糟的结果。
        var settings = await service.GetAsync();
        Assert.Equal("default", settings.Repository.Source);
        Assert.Equal(1, settings.InFlightUploads);
    }

    /// <summary>
    /// verifying 同样要拦。它不占暂存写入（Active 口径里没有它），但提交工作器正拿着
    /// 旧仓库根往里搬文件——只按"在传"判定的话，最危险的那一刻恰好是放行的。
    /// </summary>
    [Fact]
    public async Task 正在入库的会话_同样拦住换根()
    {
        ResetSettings();
        await using var provider = BuildServices();
        await SeedInFlightUploadAsync(provider, UploadStatus.Verifying);

        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(
            new UpdateStorageSettingsRequest { StagingPath = Path.Combine(_tempRoot, "staging") }));

        Assert.Equal(409, ex.StatusCode);
    }

    /// <summary>
    /// 路径原样提交不算换根，必须放行——否则"只想把并发上限从 4 改成 2"这件事，
    /// 在有传输的时候永远做不成，而那正是最想调它的时候。
    /// </summary>
    [Fact]
    public async Task 有传输没结束时_路径不动仍可改并发上限()
    {
        ResetSettings();
        await using var provider = BuildServices();

        var repository = Path.Combine(_tempRoot, "repository");
        var staging = Path.Combine(_tempRoot, "staging");
        using (var setup = provider.CreateScope())
        {
            await setup.ServiceProvider.GetRequiredService<IStorageSettingsService>().UpdateAsync(
                new UpdateStorageSettingsRequest { RepositoryPath = repository, StagingPath = staging });
        }

        await SeedInFlightUploadAsync(provider, UploadStatus.Uploading);

        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStorageSettingsService>();
        var updated = await service.UpdateAsync(new UpdateStorageSettingsRequest
        {
            RepositoryPath = repository,
            StagingPath = staging,
            MaxConcurrentUploadsTotal = 2
        });

        Assert.Equal(2, updated.MaxConcurrentUploadsTotal);
        Assert.Equal(repository, updated.Repository.EffectivePath);
    }

    /// <summary>
    /// 一个还没结束的上传会话。会话行有外键，客户端/任务/候选备份集都得跟着建出来。
    /// </summary>
    private static async Task SeedInFlightUploadAsync(ServiceProvider provider, UploadStatus status)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");

        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"storage-{suffix}",
            Hostname = $"storage-host-{suffix[..8]}",
            DisplayName = $"存储设置测试客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"storage-task-{suffix[..8]}",
            ApplicationName = "StorageSettingsTest",
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
            CandidateKey = $"storage-{suffix}",
            SourceRoot = @"D:\data\2026",
            DiscoveredAt = now,
            PrecheckStatus = PrecheckStatus.Passed,
            TotalFiles = 1,
            TotalBytes = 1024,
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
            TotalBytes = 1024,
            UploadedBytes = 512,
            ChunkSizeBytes = 8 * 1024 * 1024,
            StartedAt = now,
            LastActivityAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        db.CandidateBackupSets.Add(candidate);
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync();
    }
}
