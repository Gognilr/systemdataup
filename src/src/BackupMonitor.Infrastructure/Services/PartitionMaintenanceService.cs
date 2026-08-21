using System.Data;
using System.Data.Common;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 管理心跳和审计月度分区。所有表名由内部固定模板生成，值参数仍使用数据库参数，
/// 避免把时间或外部输入拼接为 SQL 标识符。
/// </summary>
public sealed class PartitionMaintenanceService
{
    private static readonly PartitionDefinition[] Definitions =
    [
        new("client_heartbeats", "client_heartbeats_y", "client_heartbeats_default", "received_at"),
        new("audit_logs", "audit_logs_y", "audit_logs_default", "occurred_at")
    ];

    private readonly ILogger<PartitionMaintenanceService> _logger;

    public PartitionMaintenanceService(ILogger<PartitionMaintenanceService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> EnsureFuturePartitionsAsync(
        AppDbContext db,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var firstMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var created = new List<string>();

        await WithConnectionAsync(db, async connection =>
        {
            foreach (var definition in Definitions)
            {
                await EnsureDefaultPartitionAsync(connection, definition, ct);
                for (var offset = 0; offset <= 3; offset++)
                {
                    var from = firstMonth.AddMonths(offset);
                    var to = from.AddMonths(1);
                    var childName = definition.Prefix + from.ToString("yyyy'm'MM", System.Globalization.CultureInfo.InvariantCulture);
                    if (await HasPartitionAsync(connection, definition.ParentName, childName, ct))
                        continue;

                    await CreateMonthPartitionAsync(connection, definition, childName, from, to, ct);
                    created.Add(childName);
                }
            }
        }, ct);

        return created;
    }

    public async Task<IReadOnlyList<string>> GetMissingPartitionsAsync(
        AppDbContext db,
        DateTime nowUtc,
        int horizonDays,
        CancellationToken ct = default)
    {
        var firstMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = new DateTime(
            nowUtc.AddDays(Math.Max(0, horizonDays)).Year,
            nowUtc.AddDays(Math.Max(0, horizonDays)).Month,
            1,
            0,
            0,
            0,
            DateTimeKind.Utc);
        var missing = new List<string>();

        await WithConnectionAsync(db, async connection =>
        {
            foreach (var definition in Definitions)
            {
                for (var month = firstMonth; month <= lastMonth; month = month.AddMonths(1))
                {
                    var childName = definition.Prefix + month.ToString("yyyy'm'MM", System.Globalization.CultureInfo.InvariantCulture);
                    if (!await HasPartitionAsync(connection, definition.ParentName, childName, ct))
                        missing.Add(childName);
                }
            }
        }, ct);

        return missing;
    }

    public async Task<IReadOnlyList<string>> DropExpiredHeartbeatPartitionsAsync(
        AppDbContext db,
        DateTime nowUtc,
        int retentionDays,
        CancellationToken ct = default)
    {
        var cutoff = nowUtc.AddDays(-Math.Max(1, retentionDays));
        var definition = Definitions[0];
        var dropped = new List<string>();

        await WithConnectionAsync(db, async connection =>
        {
            var children = await GetPartitionNamesAsync(connection, definition.ParentName, ct);
            foreach (var childName in children)
            {
                if (!childName.StartsWith(definition.Prefix, StringComparison.Ordinal)
                    || childName.Length != definition.Prefix.Length + 7)
                {
                    continue;
                }

                // 分区名格式为 y2026m08，直接按固定位置解析，避免本地化解析差异。
                if (!int.TryParse(childName.AsSpan(definition.Prefix.Length, 4), out var year)
                    || !int.TryParse(childName.AsSpan(definition.Prefix.Length + 5, 2), out var month)
                    || month is < 1 or > 12)
                    continue;

                var monthStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
                if (monthStart.AddMonths(1) > cutoff)
                    continue;

                await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Quote(childName)}", ct);
                dropped.Add(childName);
            }

            // DEFAULT 分区不是月度分区，不能 DROP；对其执行短批量清理，防止维护任务
            // 曾经落后时的兜底数据无限增长。
            var defaultName = Quote(definition.DefaultName);
            while (true)
            {
                var deleted = await ExecuteAsync(
                    connection,
                    $"DELETE FROM {defaultName} WHERE ctid IN (SELECT ctid FROM {defaultName} WHERE {Quote(definition.TimestampColumn)} < @cutoff LIMIT 5000)",
                    ct,
                    ("cutoff", cutoff));
                if (deleted < 5000)
                    break;
            }
        }, ct);

        return dropped;
    }

    private async Task EnsureDefaultPartitionAsync(
        DbConnection connection,
        PartitionDefinition definition,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            $"CREATE TABLE IF NOT EXISTS {Quote(definition.DefaultName)} PARTITION OF {Quote(definition.ParentName)} DEFAULT",
            ct);
    }

    private async Task CreateMonthPartitionAsync(
        DbConnection connection,
        PartitionDefinition definition,
        string childName,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        // PostgreSQL 不允许在分区界限中使用预备语句参数；日期来自内部 UTC 月初，
        // 先格式化为固定 ASCII 字面量，不接受外部输入。
        var fromLiteral = from.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var toLiteral = to.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var sql = $"CREATE TABLE IF NOT EXISTS {Quote(childName)} PARTITION OF {Quote(definition.ParentName)} FOR VALUES FROM ('{fromLiteral}') TO ('{toLiteral}')";
        try
        {
            await ExecuteAsync(connection, sql, ct);
            _logger.LogInformation("已确保分区 {Partition} 存在", childName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建分区 {Partition} 失败；请检查 DEFAULT 分区中是否已有该月份数据", childName);
            throw;
        }
    }

    private static async Task<bool> HasPartitionAsync(
        DbConnection connection,
        string parentName,
        string childName,
        CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_inherits i
                JOIN pg_class parent ON parent.oid = i.inhparent
                JOIN pg_class child ON child.oid = i.inhrelid
                JOIN pg_namespace ns ON ns.oid = parent.relnamespace
                WHERE ns.nspname = current_schema()
                  AND parent.relname = @parent
                  AND child.relname = @child)
            """;
        return Convert.ToBoolean(await ExecuteScalarAsync(connection, sql, ct, P("parent", parentName), P("child", childName)));
    }

    private static async Task<IReadOnlyList<string>> GetPartitionNamesAsync(
        DbConnection connection,
        string parentName,
        CancellationToken ct)
    {
        const string sql = """
            SELECT child.relname
            FROM pg_inherits i
            JOIN pg_class parent ON parent.oid = i.inhparent
            JOIN pg_class child ON child.oid = i.inhrelid
            JOIN pg_namespace ns ON ns.oid = parent.relnamespace
            WHERE ns.nspname = current_schema()
              AND parent.relname = @parent
            """;
        return (await ExecuteRowsAsync(connection, sql, ct, P("parent", parentName)))
            .ToArray();
    }

    private static async Task WithConnectionAsync(
        AppDbContext db,
        Func<DbConnection, Task> action,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await db.Database.OpenConnectionAsync(ct);
        try
        {
            await action(connection);
        }
        finally
        {
            if (shouldClose)
                await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ExecuteScalarAsync(
        DbConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task<IReadOnlyList<string>> ExecuteRowsAsync(
        DbConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct))
            rows.Add(reader.GetString(0));
        return rows;
    }

    private static void AddParameters(DbCommand command, IReadOnlyList<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private static (string Name, object Value) P(string name, object value) => (name, value);

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record PartitionDefinition(
        string ParentName,
        string Prefix,
        string DefaultName,
        string TimestampColumn);
}
