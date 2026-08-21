using System.Security.Cryptography;
using System.Text;
using BackupMonitor.Infrastructure.Database;
using Npgsql;

namespace BackupMonitor.Infrastructure.Tests;

[Collection("postgres")]
public sealed class MigrationRunnerTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public MigrationRunnerTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Production_runner_records_canonical_v005_and_allows_passwordless_upgrade_rerun()
    {
        var v005Path = Path.Combine(_fixture.MigrationDirectory, "V005__admin_password_bootstrap.sql");
        var v005Sql = await File.ReadAllTextAsync(v005Path, Encoding.UTF8);
        var expectedChecksum = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(v005Sql)))
            .ToLowerInvariant();

        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT m.checksum,
                       u.password_hash,
                       u.must_change_password
                FROM schema_migrations AS m
                JOIN users AS u ON u.username = 'admin'
                WHERE m.version = 'V005__admin_password_bootstrap'
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(expectedChecksum, reader.GetString(0));
            Assert.False(reader.IsDBNull(1));
            Assert.True(reader.GetBoolean(2));
        }

        await new MigrationRunner().ApplyAsync(
            _fixture.ConnectionString,
            _fixture.MigrationDirectory,
            adminPassword: null,
            progress: null,
            CancellationToken.None);

        await using var verify = new NpgsqlConnection(_fixture.ConnectionString);
        await verify.OpenAsync();
        await using var passwordCheck = new NpgsqlCommand(
            "SELECT crypt(@password, password_hash) = password_hash FROM users WHERE username = 'admin'",
            verify);
        passwordCheck.Parameters.AddWithValue("password", PostgresDatabaseFixture.AdminBootstrapPassword);
        Assert.True((bool?)await passwordCheck.ExecuteScalarAsync());
    }
}
