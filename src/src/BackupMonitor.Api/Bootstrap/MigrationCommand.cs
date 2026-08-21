using BackupMonitor.Infrastructure.Database;
using Npgsql;

namespace BackupMonitor.Api.Bootstrap;

/// <summary>
/// dbinit.bat 使用的非交互迁移入口。API EXE 不需要安装器的管理员 manifest，
/// 但仍调用与安装器完全相同的 MigrationRunner；管理员口令只从环境变量读取。
/// </summary>
internal static class MigrationCommand
{
    private const string CommandName = "--migrate";

    public static bool IsMigrationCommand(string[] args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args)
    {
        var logPath = Environment.GetEnvironmentVariable("BACKUPMONITOR_MIGRATION_LOG");
        try
        {
            var connectionString = BuildConnectionString();
            var migrationDirectory = GetMigrationDirectory(args);
            var adminPassword = FirstNonEmpty(
                Environment.GetEnvironmentVariable("BACKUPMONITOR_MIGRATION_ADMIN_PASSWORD"),
                Environment.GetEnvironmentVariable("ADMIN_PW"));

            Write(logPath, $"开始应用迁移：{migrationDirectory}");
            await new MigrationRunner().ApplyAsync(
                connectionString,
                migrationDirectory,
                adminPassword,
                new Progress<string>(message => Write(logPath, message)),
                CancellationToken.None);
            Write(logPath, "迁移完成。");
            return 0;
        }
        catch (Exception ex)
        {
            Write(logPath, $"迁移失败：{ex.Message}");
            return 1;
        }
    }

    private static string BuildConnectionString()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("BACKUPMONITOR_MIGRATION_CONNECTION");
        if (!string.IsNullOrWhiteSpace(explicitConnection))
            return explicitConnection;

        var password = Environment.GetEnvironmentVariable("PGPASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("PGPASSWORD 未设置。");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = FirstNonEmpty(Environment.GetEnvironmentVariable("PGHOST"), "localhost")!,
            Port = ParsePort(Environment.GetEnvironmentVariable("PGPORT"), 5432),
            Database = FirstNonEmpty(Environment.GetEnvironmentVariable("PGDATABASE"), "backup_monitor")!,
            Username = FirstNonEmpty(Environment.GetEnvironmentVariable("PGUSER"), "postgres")!,
            Password = password,
            IncludeErrorDetail = false
        };
        return builder.ConnectionString;
    }

    private static string GetMigrationDirectory(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--migration-directory", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[i + 1]));
        }

        var configured = Environment.GetEnvironmentVariable("BACKUPMONITOR_MIGRATION_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));

        throw new InvalidOperationException("未设置迁移目录，请提供 --migration-directory 或 BACKUPMONITOR_MIGRATION_DIRECTORY。");
    }

    private static void Write(string? logPath, string message)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return;

        try
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(logPath));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志不可写时保留原始退出码，不遮蔽迁移结果。
        }
    }

    private static int ParsePort(string? value, int fallback) =>
        int.TryParse(value, out var port) && port is > 0 and <= 65535 ? port : fallback;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
