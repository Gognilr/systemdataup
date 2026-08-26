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
            "UPDATE system_settings SET setting_value = 'null'::jsonb WHERE setting_key IN ('repository_path', 'staging_path')",
            connection);
        command.ExecuteNonQuery();
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
}
