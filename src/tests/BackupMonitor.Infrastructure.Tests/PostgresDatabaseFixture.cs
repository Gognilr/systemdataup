using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;
using BackupMonitor.Infrastructure.Database;
using Testcontainers.PostgreSql;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 数据库夹具：通过 Testcontainers 启动一次性 PostgreSQL 容器，
/// 并按顺序执行 database/V001~V010 迁移脚本，得到与生产一致的真实库结构。
/// 整个测试集合共享一个容器，避免重复拉取/启动开销。
/// </summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    /// <summary>仅用于本地测试数据库的管理员引导口令，不进入产品运行时配置。</summary>
    public const string AdminBootstrapPassword = "Test-Admin-Pw-2026!";

    private PostgreSqlContainer? _container;
    private string? _localDataDirectory;
    private string? _localPostgresRoot;

    /// <summary>容器连接串（postgres 超级用户）。</summary>
    public string ConnectionString { get; private set; } = null!;

    public string MigrationDirectory => FindDatabaseDirectory();

    public async Task InitializeAsync()
    {
        if (UseLocalPostgres)
        {
            await StartLocalPostgresAsync();
        }
        else
        {
            await StartContainerAsync();
        }

        await new MigrationRunner().ApplyAsync(
            ConnectionString,
            FindDatabaseDirectory(),
            AdminBootstrapPassword,
            progress: null,
            CancellationToken.None);
    }

    private async Task StartContainerAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16")
            .WithName($"bm-infra-tests-{Guid.NewGuid():N}")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    private async Task StartLocalPostgresAsync()
    {
        _localPostgresRoot = Environment.GetEnvironmentVariable("BACKUPMONITOR_POSTGRES_ROOT")
            ?? @"C:\Program Files\PostgreSQL\16";
        var initDb = Path.Combine(_localPostgresRoot, "bin", "initdb.exe");
        var pgCtl = Path.Combine(_localPostgresRoot, "bin", "pg_ctl.exe");
        if (!File.Exists(initDb) || !File.Exists(pgCtl))
            throw new FileNotFoundException(
                $"本地 PostgreSQL 测试回退需要 initdb.exe 和 pg_ctl.exe：{_localPostgresRoot}");

        _localDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "BackupMonitor.Infrastructure.Tests",
            $"postgres-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(_localDataDirectory)!);
        var port = FindFreePort();

        await RunToolAsync(initDb, ["-D", _localDataDirectory, "-U", "postgres", "--auth=trust", "--no-locale", "--encoding=UTF8"]);
        await RunToolAsync(
            pgCtl,
            ["-D", _localDataDirectory, "-o", $"-p {port} -h 127.0.0.1", "-w", "start"],
            captureOutput: false);

        ConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "postgres",
            Username = "postgres",
            Timeout = 10,
            CommandTimeout = 120
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
    }

    private static bool UseLocalPostgres =>
        string.Equals(
            Environment.GetEnvironmentVariable("BACKUPMONITOR_TEST_LOCAL_POSTGRES"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task RunToolAsync(
        string fileName,
        IEnumerable<string> arguments,
        bool captureOutput = true)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = captureOutput
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException($"无法启动 PostgreSQL 测试工具：{fileName}");

        await process.WaitForExitAsync();
        var output = captureOutput ? await process.StandardOutput.ReadToEndAsync() : string.Empty;
        var error = captureOutput ? await process.StandardError.ReadToEndAsync() : string.Empty;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"PostgreSQL 测试工具 {Path.GetFileName(fileName)} 退出码 {process.ExitCode}：{error.Trim()} {output.Trim()}");
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
        if (_localDataDirectory is not null && _localPostgresRoot is not null)
        {
            var pgCtl = Path.Combine(_localPostgresRoot, "bin", "pg_ctl.exe");
            await RunToolAsyncAllowFailure(pgCtl, ["-D", _localDataDirectory, "-m", "fast", "-w", "stop"]);
            if (Directory.Exists(_localDataDirectory))
                Directory.Delete(_localDataDirectory, recursive: true);
        }
    }

    private static async Task RunToolAsyncAllowFailure(string fileName, IEnumerable<string> arguments)
    {
        try
        {
            await RunToolAsync(fileName, arguments);
        }
        catch
        {
            // 测试清理路径必须尽力停止临时实例，不能覆盖原始测试结果。
        }
    }

    private static string FindDatabaseDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "database");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "V001__initial_schema.sql")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "未找到 database/V001__initial_schema.sql，请确认测试在仓库目录结构内运行。");
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresDatabaseFixture>
{
}
