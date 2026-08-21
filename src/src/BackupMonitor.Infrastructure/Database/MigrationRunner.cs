using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace BackupMonitor.Infrastructure.Database;

/// <summary>
/// 安装器、升级流程和 dbinit.bat 共用的 SQL 迁移运行器。
/// 迁移脚本是唯一的业务实现；V005 的管理员口令只通过 Npgsql 参数注入，
/// 不进入命令行、SQL 日志或脚本文件。
/// </summary>
public class MigrationRunner
{
    private const long MigrationAdvisoryLockKey = 2026081301;
    private const string AdminPasswordPlaceholder = "${ADMIN_PASSWORD}";

    // 兼容此前由旧版重复实现写入的 V005 checksum；只允许这一已知版本迁移到统一脚本。
    private const string LegacyV005Checksum =
        "e3352e31c3735befa976c7ca49f082af7c123b03c76b417b300c000521841ccd";

    public async Task ApplyAsync(
        string connectionString,
        string migrationDirectory,
        string? adminPassword,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var scripts = Directory.GetFiles(migrationDirectory, "V*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        if (scripts.Length == 0)
            throw new DirectoryNotFoundException($"没有找到迁移脚本：{migrationDirectory}");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var lockAcquired = false;
        try
        {
            await AcquireMigrationLockAsync(connection, ct);
            lockAcquired = true;
            await EnsureMigrationTableAsync(connection, ct);

            foreach (var script in scripts)
            {
                ct.ThrowIfCancellationRequested();
                var version = Path.GetFileNameWithoutExtension(script);
                var sql = await File.ReadAllTextAsync(script, Encoding.UTF8, ct);
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
                var applied = await GetAppliedChecksumAsync(connection, version, ct);
                if (applied is not null)
                {
                    if (!string.Equals(applied, checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsKnownLegacyChecksum(version, applied))
                            throw new InvalidOperationException($"迁移 {version} 已应用但 checksum 不一致，拒绝继续。");

                        await UpdateAppliedChecksumAsync(connection, version, applied, checksum, ct);
                        progress?.Report($"已将迁移 {version} 的旧实现标记为统一脚本");
                    }
                    else
                    {
                        progress?.Report($"已跳过迁移 {version}");
                    }

                    continue;
                }

                var isV005 = version.StartsWith("V005_", StringComparison.OrdinalIgnoreCase);
                if (isV005 && string.IsNullOrWhiteSpace(adminPassword))
                    throw new InvalidOperationException("首次安装需要管理员口令才能应用 V005；升级流程不会修改管理员口令，请使用“重置管理员密码”。");

                progress?.Report($"正在应用迁移 {version}");
                await using var transaction = await connection.BeginTransactionAsync(ct);
                try
                {
                    await ExecuteMigrationAsync(
                        connection,
                        transaction,
                        version,
                        sql,
                        adminPassword,
                        ct);

                    await using var record = new NpgsqlCommand(
                        "INSERT INTO schema_migrations(version, applied_at, checksum) VALUES (@version, now(), @checksum)",
                        connection,
                        transaction);
                    record.Parameters.AddWithValue("version", version);
                    record.Parameters.AddWithValue("checksum", checksum);
                    await record.ExecuteNonQueryAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch (InvalidOperationException)
                    {
                        // Npgsql 已在服务器端结束事务时保留原始迁移异常。
                    }
                    throw;
                }
            }
        }
        finally
        {
            if (lockAcquired && connection.State == System.Data.ConnectionState.Open)
                await ReleaseMigrationLockAsync(connection);
        }
    }

    private static async Task ExecuteMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string version,
        string sql,
        string? adminPassword,
        CancellationToken ct)
    {
        // V001 是历史上可由 psql 直接执行的脚本，带有顶层 BEGIN/COMMIT。
        // 运行器自身为每个版本建立事务，因此只移除这两个独立事务控制行，保留函数体内的 BEGIN/END。
        var executionSql = Regex.Replace(
            sql,
            @"(?im)^\s*(BEGIN|COMMIT)\s*;\s*$",
            string.Empty);
        var hasAdminPasswordPlaceholder = executionSql.Contains(AdminPasswordPlaceholder, StringComparison.Ordinal);
        var isV005 = version.StartsWith("V005_", StringComparison.OrdinalIgnoreCase);
        if (isV005 != hasAdminPasswordPlaceholder)
            throw new InvalidOperationException($"迁移 {version} 的管理员口令占位符不符合统一迁移契约。");

        var commandText = executionSql;
        await using var command = new NpgsqlCommand(commandText, connection, transaction)
        {
            CommandTimeout = 180
        };
        if (hasAdminPasswordPlaceholder)
        {
            if (string.IsNullOrWhiteSpace(adminPassword))
                throw new InvalidOperationException("迁移脚本需要管理员口令，但当前运行没有提供口令。");

            command.CommandText = commandText.Replace(
                AdminPasswordPlaceholder,
                "@admin_password",
                StringComparison.Ordinal);
            command.Parameters.AddWithValue("admin_password", adminPassword);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool IsKnownLegacyChecksum(string version, string applied) =>
        version.StartsWith("V005_", StringComparison.OrdinalIgnoreCase)
        && string.Equals(applied, LegacyV005Checksum, StringComparison.OrdinalIgnoreCase);

    private static async Task AcquireMigrationLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(@lock_key)",
            connection);
        command.Parameters.AddWithValue("lock_key", MigrationAdvisoryLockKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReleaseMigrationLockAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(@lock_key)",
                connection);
            command.Parameters.AddWithValue("lock_key", MigrationAdvisoryLockKey);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch
        {
            // 连接释放时 PostgreSQL 会自动释放会话级 advisory lock，不能遮蔽原始迁移错误。
        }
    }

    private static async Task EnsureMigrationTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version varchar(128) PRIMARY KEY,
                applied_at timestamptz NOT NULL,
                checksum varchar(64) NOT NULL
            )
            """,
            connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> GetAppliedChecksumAsync(
        NpgsqlConnection connection,
        string version,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT checksum FROM schema_migrations WHERE version = @version",
            connection);
        command.Parameters.AddWithValue("version", version);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task UpdateAppliedChecksumAsync(
        NpgsqlConnection connection,
        string version,
        string expectedOldChecksum,
        string newChecksum,
        CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand(
                "UPDATE schema_migrations SET checksum = @new_checksum WHERE version = @version AND checksum = @old_checksum",
                connection,
                transaction);
            command.Parameters.AddWithValue("version", version);
            command.Parameters.AddWithValue("old_checksum", expectedOldChecksum);
            command.Parameters.AddWithValue("new_checksum", newChecksum);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException($"迁移 {version} 的旧 checksum 更新失败，拒绝继续。");

            await transaction.CommitAsync(ct);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Npgsql 已在服务器端结束事务时保留原始 checksum 异常。
            }
            throw;
        }
    }
}
