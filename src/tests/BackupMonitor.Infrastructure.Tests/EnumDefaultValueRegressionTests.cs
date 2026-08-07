using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// OPEN-ISSUES #1 回归测试：枚举列默认值缺陷。
/// 缺陷形态：EF 配置 HasDefaultValue(非0号枚举成员) 时，显式写入 0 号成员
/// （TaskMode.Automatic / ImportanceLevel.Low）会被 EF 判定为"未设置"而省略列，
/// 数据库默认值（approval_required / normal）悄悄覆盖业务意图。
/// 修复方式：移除 HasDefaultValue，默认语义由实体初始化器承担。
/// </summary>
[Collection("postgres")]
public class EnumDefaultValueRegressionTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public EnumDefaultValueRegressionTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// 显式设置 TaskMode=Automatic、ImportanceLevel=Low 后保存，
    /// 重新查询（新上下文 + AsNoTracking）必须原样读回这两个 0 号成员值，
    /// 且数据库中的原始文本必须是 'automatic' / 'low'。
    /// 若回滚修复（恢复 HasDefaultValue），本测试应当失败。
    /// </summary>
    [Fact]
    public async Task 显式写入零号枚举成员_保存后不得被数据库默认值覆盖()
    {
        Guid taskId;

        await using (var context = CreateContext())
        {
            var client = new Client
            {
                MachineId = $"unit-test-{Guid.NewGuid():N}",
                Hostname = "unit-test-host",
                DisplayName = "枚举默认值回归测试客户端"
            };
            context.Clients.Add(client);
            await context.SaveChangesAsync();

            var task = new BackupTask
            {
                ClientId = client.Id,
                Name = "枚举默认值回归测试任务",
                ApplicationName = "UnitTestFixture",
                SourcePath = "/tmp/unit-test-source",
                TaskMode = TaskMode.Automatic,
                ImportanceLevel = ImportanceLevel.Low
            };
            context.BackupTasks.Add(task);
            await context.SaveChangesAsync();
            taskId = task.Id;
        }

        // 新上下文重新查询，排除变更跟踪缓存造成的假阳性
        await using (var context = CreateContext())
        {
            var reloaded = await context.BackupTasks
                .AsNoTracking()
                .SingleAsync(t => t.Id == taskId);

            Assert.Equal(TaskMode.Automatic, reloaded.TaskMode);
            Assert.Equal(ImportanceLevel.Low, reloaded.ImportanceLevel);
        }

        // 绕过 EF 直接校验库中原始值，确认真的写入了 0 号成员
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT task_mode, importance_level FROM backup_tasks WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", taskId);

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("automatic", reader.GetString(0));
            Assert.Equal("low", reader.GetString(1));
        }
    }

    /// <summary>
    /// 对照组：不显式赋值时，实体初始化器应保证默认语义仍为
    /// ApprovalRequired / Normal（与原数据库默认值一致），行为不回退。
    /// </summary>
    [Fact]
    public async Task 未显式赋值_实体初始化器默认值保持不变()
    {
        Guid taskId;

        await using (var context = CreateContext())
        {
            var client = new Client
            {
                MachineId = $"unit-test-{Guid.NewGuid():N}",
                Hostname = "unit-test-host-2",
                DisplayName = "枚举默认值对照组客户端"
            };
            context.Clients.Add(client);
            await context.SaveChangesAsync();

            var task = new BackupTask
            {
                ClientId = client.Id,
                Name = "枚举默认值对照组任务",
                ApplicationName = "UnitTestFixture",
                SourcePath = "/tmp/unit-test-source-2"
            };
            context.BackupTasks.Add(task);
            await context.SaveChangesAsync();
            taskId = task.Id;
        }

        await using (var context = CreateContext())
        {
            var reloaded = await context.BackupTasks
                .AsNoTracking()
                .SingleAsync(t => t.Id == taskId);

            Assert.Equal(TaskMode.ApprovalRequired, reloaded.TaskMode);
            Assert.Equal(ImportanceLevel.Normal, reloaded.ImportanceLevel);
        }
    }
}
