using Npgsql;
using Testcontainers.PostgreSql;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 数据库夹具：通过 Testcontainers 启动一次性 PostgreSQL 容器，
/// 并按顺序执行 database/V001~V005 迁移脚本，得到与生产一致的真实库结构。
/// 整个测试集合共享一个容器，避免重复拉取/启动开销。
/// </summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    /// <summary>
    /// 测试环境注入的管理员引导口令。V005 中的 admin_pw 是 psql 变量（:'admin_pw'），
    /// Npgsql 无法解析，执行前统一替换为本常量（生产由 dbinit.bat 注入 ADMIN_PW）。
    /// </summary>
    public const string AdminBootstrapPassword = "Test-Admin-Pw-2026!";

    private PostgreSqlContainer _container = null!;

    /// <summary>容器连接串（postgres 超级用户）。</summary>
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:15")
            .WithName($"bm-infra-tests-{Guid.NewGuid():N}")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        foreach (var script in MigrationScripts)
        {
            var sql = File.ReadAllText(script)
                // psql 变量只能在 psql 中求值，这里替换为测试常量（常量不含单引号，拼接安全）
                .Replace(":'admin_pw'", $"'{AdminBootstrapPassword}'");
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// 迁移脚本按版本号顺序执行。脚本目录从测试程序集位置向上查找
    /// （兼容 bin/Debug/net8.0 等多层输出目录）。
    /// </summary>
    private static IReadOnlyList<string> MigrationScripts
    {
        get
        {
            var databaseDir = FindDatabaseDirectory();
            return Directory.GetFiles(databaseDir, "V*.sql")
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
                .ToList();
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
