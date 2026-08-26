using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using Npgsql;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// C# 枚举与数据库 DOMAIN 的 CHECK 约束必须一一对应。
///
/// 这两处是分开维护的：枚举加一个值只要改 Enums.cs，而 DOMAIN 要写一条迁移。
/// 漏掉迁移不会在编译期报错，也不会在大多数测试里暴露——它会在生产上第一次
/// 真正写入这个新值时才炸，报的还是一句 PostgreSQL 的约束违反，
/// 跟"某个功能用不了"之间隔着好几层。这条测试就是为了把那一刻提前到 CI。
///
/// browse_path（V015）是第一个由它守住的新增值。
/// </summary>
[Collection("postgres")]
public class EnumDomainParityTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public EnumDomainParityTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string, string[]> Domains() => new()
    {
        { "command_type", Values<CommandType>() },
        { "command_status", Values<CommandStatus>() },
        { "recognizer_type", Values<RecognizerType>() },
        { "task_mode", Values<TaskMode>() },
        { "precheck_status", Values<PrecheckStatus>() },
        { "client_status", Values<ClientStatus>() },
        { "importance_level", Values<ImportanceLevel>() },
        { "audit_result", Values<AuditResult>() }
    };

    private static string[] Values<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(EnumMapping.ToSnakeCase).ToArray();

    [Theory]
    [MemberData(nameof(Domains))]
    public async Task 枚举值全部被数据库域接受(string domainName, string[] expectedValues)
    {
        var definition = await ReadDomainCheckAsync(domainName);
        Assert.NotNull(definition);

        var missing = expectedValues
            .Where(value => !definition!.Contains($"'{value}'", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"枚举值在 {domainName} 的 CHECK 约束里不存在：{string.Join("、", missing)}。"
            + "新增枚举值需要同时写一条迁移扩充这个域。");
    }

    /// <summary>
    /// 反向：域里不该有枚举不认识的值。多出来的值意味着代码永远处理不了它，
    /// 而它可能已经躺在某张表里了。
    /// </summary>
    [Theory]
    [MemberData(nameof(Domains))]
    public async Task 数据库域里没有代码不认识的值(string domainName, string[] expectedValues)
    {
        var definition = await ReadDomainCheckAsync(domainName);
        Assert.NotNull(definition);

        var declared = System.Text.RegularExpressions.Regex
            .Matches(definition!, @"'([a-z0-9_]+)'")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        var unexpected = declared.Except(expectedValues, StringComparer.Ordinal).ToList();
        Assert.True(
            unexpected.Count == 0,
            $"{domainName} 的 CHECK 约束里有代码没有对应枚举值的取值：{string.Join("、", unexpected)}");
    }

    private async Task<string?> ReadDomainCheckAsync(string domainName)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_constraintdef(con.oid)
              FROM pg_constraint con
              JOIN pg_type typ ON typ.oid = con.contypid
             WHERE typ.typname = @domain
               AND con.contype = 'c'
             LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("domain", domainName);
        return (await command.ExecuteScalarAsync()) as string;
    }
}
