using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 配置备份包的现状判定（整改清单 R4）。
///
/// 导出是手动触发的（2026-09-10 定板：不做定时自动导出），
/// 于是「超期告警」是唯一的兜底——没有任何定时任务替人记着这件事。
/// 这几条钉的是这个判定的三个要害：
///   1. 从没导出过 = 超期（这恰恰是最该被看见的状态，不能因为算不出天数就显示成正常）
///   2. 刚导出过 = 不超期
///   3. 上次导出时间取自目录里的文件而不是导出记录表——
///      服务管理台本机导出的包不经过 API，只看表会把它们全漏掉，
///      于是「明明昨天刚导过」却报超期。
/// </summary>
[Collection("postgres")]
public class ConfigBackupStatusTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private string _dataDirectory = null!;

    public ConfigBackupStatusTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(
            Path.GetTempPath(), "BackupMonitor.ConfigBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 从未导出过配置备份包时判为超期()
    {
        await using var services = BuildServices();
        var status = await StatusAsync(services);

        Assert.True(status.Overdue);
        Assert.Null(status.LatestExportedAt);
        Assert.Null(status.AgeDays);
        Assert.Equal(0, status.PackageCount);
    }

    [Fact]
    public async Task 刚导出过时不判超期()
    {
        WritePackage("BackupMonitor-backup-20260910-030000.bmbp", DateTime.UtcNow.AddHours(-2));

        await using var services = BuildServices();
        var status = await StatusAsync(services);

        Assert.False(status.Overdue);
        Assert.Equal(0, status.AgeDays);
        Assert.Equal(1, status.PackageCount);
    }

    /// <summary>目录里有包但都太老——手动模式下这正是「没人记着这件事」的样子。</summary>
    [Fact]
    public async Task 上次导出超过阈值时判为超期()
    {
        WritePackage("BackupMonitor-backup-20260601-030000.bmbp",
            DateTime.UtcNow.AddDays(-ConfigBackupService.DefaultOverdueDays - 5));

        await using var services = BuildServices();
        var status = await StatusAsync(services);

        Assert.True(status.Overdue);
        Assert.Equal(ConfigBackupService.DefaultOverdueDays + 5, status.AgeDays);
    }

    /// <summary>
    /// 上次导出时间必须取目录里**最新**的那个文件。
    /// 这一条同时钉住「本机导出也算数」：那些包只是目录里的文件，表里没有对应行。
    /// </summary>
    [Fact]
    public async Task 上次导出时间取目录里最新的那份()
    {
        WritePackage("old.bmbp", DateTime.UtcNow.AddDays(-100));
        WritePackage("new.bmbp", DateTime.UtcNow.AddDays(-1));

        await using var services = BuildServices();
        var status = await StatusAsync(services);

        Assert.Equal("new.bmbp", status.LatestFileName);
        Assert.Equal(1, status.AgeDays);
        Assert.False(status.Overdue);
        Assert.Equal(2, status.PackageCount);
    }

    // ---------- 基础设施 ----------

    private void WritePackage(string fileName, DateTime writtenAtUtc)
    {
        var directory = ConfigBackupPaths.DirectoryFor(_dataDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "not a real package, only the timestamp matters here");
        File.SetLastWriteTimeUtc(path, writtenAtUtc);
    }

    private static Task<Shared.Models.Admin.ConfigBackupStatusDto> StatusAsync(ServiceProvider services)
    {
        var scope = services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IConfigBackupService>();
        return service.GetStatusAsync();
    }

    private ServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:DataDirectory"] = _dataDirectory
            })
            .Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IConfigBackupService, ConfigBackupService>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        return sc.BuildServiceProvider();
    }
}
