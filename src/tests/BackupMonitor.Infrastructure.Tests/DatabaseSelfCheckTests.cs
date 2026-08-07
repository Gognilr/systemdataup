using BackupMonitor.Core.Entities.System;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// OPEN-ISSUES #6：/health/db 端点只读化后，原有的乐观锁与枚举映射自检
/// 从运行时端点迁入测试工程。这些自检会写库（探针行），不适合放在只读健康检查里，
/// 但在真实 PostgreSQL 上定期回归仍然有价值。
/// </summary>
[Collection("postgres")]
public class DatabaseSelfCheckTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public DatabaseSelfCheckTests(PostgresDatabaseFixture fixture)
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
    /// 乐观锁自检（原 /health/db 探针逻辑）：
    /// 插入探针行后每次更新 row_version 递增；第二个上下文持旧版本号更新时
    /// 必须触发 DbUpdateConcurrencyException，保证并发写不会静默丢失。
    /// 探针行测试结束即删除，不在共享库中留痕。
    /// </summary>
    [Fact]
    public async Task 乐观锁_版本号递增且并发冲突可检测()
    {
        // 每次运行独立探针键，避免并行测试或残留干扰
        var probeKey = $"diag_probe_{Guid.NewGuid():N}";

        await using var db = CreateContext();
        SystemSetting? probe = null;
        try
        {
            probe = new SystemSetting { SettingKey = probeKey, SettingValue = "1", Encrypted = false };
            db.SystemSettings.Add(probe);
            await db.SaveChangesAsync();
            var versionAfterInsert = probe.RowVersion;

            probe.SettingValue = "2";
            await db.SaveChangesAsync();
            var versionAfterFirstUpdate = probe.RowVersion;

            // 第二个上下文模拟并发：先读到旧版本号
            await using var ctx2 = CreateContext();
            var probe2 = await ctx2.SystemSettings.SingleAsync(s => s.SettingKey == probeKey);

            probe.SettingValue = "3";
            await db.SaveChangesAsync(); // 先提交成功，row_version 再 +1
            var versionAfterSecondUpdate = probe.RowVersion;

            // 版本号必须逐次递增
            Assert.True(versionAfterFirstUpdate > versionAfterInsert);
            Assert.True(versionAfterSecondUpdate > versionAfterFirstUpdate);

            // 持旧版本号的并发写必须冲突
            probe2.SettingValue = "99";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctx2.SaveChangesAsync());
        }
        finally
        {
            if (probe is not null)
            {
                db.SystemSettings.Remove(probe);
                await db.SaveChangesAsync();
            }
        }

        // 确认探针行已清理
        await using (var verify = CreateContext())
        {
            Assert.False(await verify.SystemSettings.AnyAsync(s => s.SettingKey == probeKey));
        }
    }

    /// <summary>
    /// 枚举映射自检：backup_task_templates 种子数据的 recognizer_type 原始文本
    /// 必须与 EF 读出的枚举值经 EnumMapping.ToSnakeCase 转换后一一对应，
    /// 保证 DOMAIN 域类型与 C# 枚举双向映射不失真。
    /// </summary>
    [Fact]
    public async Task 枚举映射_种子模板识别类型与原始文本一致()
    {
        // EF 侧：读出全部模板的枚举值
        await using var db = CreateContext();
        var templates = await db.BackupTaskTemplates.AsNoTracking()
            .OrderBy(t => t.Code)
            .Select(t => new { t.Code, t.RecognizerType })
            .ToListAsync();
        Assert.NotEmpty(templates);

        // 原始 SQL 侧：直接读数据库中的 snake_case 文本
        var rawValues = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT code, recognizer_type FROM backup_task_templates", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rawValues[reader.GetString(0)] = reader.GetString(1);
        }

        foreach (var template in templates)
        {
            Assert.True(rawValues.ContainsKey(template.Code), $"缺少模板 {template.Code} 的原始记录");
            var expected = EnumMapping.ToSnakeCase(template.RecognizerType);
            Assert.Equal(rawValues[template.Code], expected);
        }

        // 抽查具体已知种子值，防止枚举整体改名后 ToSnakeCase 仍自洽的假阳性
        Assert.Equal(EnumMapping.ToSnakeCase(RecognizerType.LatestSingleFile), rawValues["tpl_latest_file"]);
        Assert.Equal(EnumMapping.ToSnakeCase(RecognizerType.MultiFileSet), rawValues["tpl_multi_file"]);
    }

    /// <summary>
    /// 种子数据完整性自检：admin 账号存在且状态为 Active，角色表非空。
    /// </summary>
    [Fact]
    public async Task 种子数据_admin账号与角色齐备()
    {
        await using var db = CreateContext();

        var admin = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == "admin");
        Assert.NotNull(admin);
        Assert.Equal(UserStatus.Active, admin!.Status);

        var roleCount = await db.Roles.CountAsync();
        Assert.True(roleCount > 0);
    }
}
