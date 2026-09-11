using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 告警链路的三条（C3 / D6 / D9）。
///
/// 共同点是「系统以为自己已经说过了」：去重键落错地方让同一件事刷成十几条、
/// 通知发不出去只写进 error_message、一条严重告警挂三天再无音讯——
/// 每一条都让告警中心变得更不可信一点，而告警中心是这个产品唯一的输出端。
/// </summary>
[Collection("postgres")]
public class AlertChainTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public AlertChainTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(SigningConfiguration());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddSingleton<CommandSigner>();
        sc.AddScoped<CommandService>();
        sc.AddScoped<ICommandDispatcher>(sp => sp.GetRequiredService<CommandService>());
        sc.AddScoped<IAgentCommandService>(sp => sp.GetRequiredService<CommandService>());
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---------- C3 ----------

    /// <summary>
    /// 同一个任务反复上传失败，只该有一条告警在累加，而不是每条指令攒一条。
    ///
    /// 去重键原先是 command:{commandId}:failed。一条上传失败之后指令会被复位重发或新建，
    /// 每一次都是新的 commandId，于是同一个故障在告警中心攒出十几条一模一样的记录，
    /// 而每一条的 occurrence_count 都是 1——看不出它到底反复了多少次。
    /// </summary>
    [Fact]
    public async Task 同一个任务多次上传失败只产生一条告警()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();

        for (var i = 0; i < 3; i++)
            await FailUploadCommandAsync(clientId, taskId);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => a.TaskId == taskId && a.Category == "upload_failed")
            .ToListAsync();

        Assert.Single(alerts);
        Assert.Equal(3, alerts[0].OccurrenceCount);
        Assert.Equal($"task:{taskId}:upload_failed", alerts[0].AlertKey);
    }

    // ---------- D6 ----------

    /// <summary>
    /// 长期未恢复的高等级告警要按间隔重发通知。
    ///
    /// 原先只有「新建」和「等级提升」两个通知触发点：一条 Critical 挂在那里三天没人处理，
    /// 系统只在第一分钟发过一封邮件——而「没有新邮件」在收件人那里读起来和「已经好了」一样。
    /// </summary>
    [Fact]
    public async Task 长期未恢复的告警按间隔重发通知()
    {
        await EnableEmailChannelAsync();
        var key = $"test:{Guid.NewGuid():N}:renotify";

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlertingService>()
                .RaiseAsync(key, AlertLevel.Critical, "unit_test", "磁盘快满了", "可用率 3%");
        }

        Assert.Equal(1, await DeliveryCountAsync(key));

        // 第二次触发紧接着发生：还在重发间隔之内，不该再发一封
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlertingService>()
                .RaiseAsync(key, AlertLevel.Critical, "unit_test", "磁盘快满了", "可用率 3%");
        }

        Assert.Equal(1, await DeliveryCountAsync(key));

        // 把「上一次通知」推到两天前：这条告警已经挂了两天没人管
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Alerts.Where(a => a.AlertKey == key)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.LastNotifiedAt, DateTime.UtcNow.AddDays(-2))
                    .SetProperty(a => a.FirstOccurredAt, DateTime.UtcNow.AddDays(-2)));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlertingService>()
                .RaiseAsync(key, AlertLevel.Critical, "unit_test", "磁盘快满了", "可用率 3%");
        }

        var deliveries = await DeliveriesAsync(key);
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, d => d.TitlePrefix == "【仍未恢复】");
    }

    /// <summary>Notice 级不参与重发——反复打扰一条「提示」只会让人开始忽略全部通知。</summary>
    [Fact]
    public async Task 提示级告警不参与重发()
    {
        await EnableEmailChannelAsync();
        var key = $"test:{Guid.NewGuid():N}:notice";

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlertingService>()
                .RaiseAsync(key, AlertLevel.Notice, "unit_test", "一条提示");
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Alerts.Where(a => a.AlertKey == key)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.LastNotifiedAt, DateTime.UtcNow.AddDays(-30))
                    .SetProperty(a => a.FirstOccurredAt, DateTime.UtcNow.AddDays(-30)));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlertingService>()
                .RaiseAsync(key, AlertLevel.Notice, "unit_test", "一条提示");
        }

        Assert.Equal(1, await DeliveryCountAsync(key));
    }

    /// <summary>
    /// 默认筛选（Critical + Warning）下，提示级告警不进邮箱——**但告警本身照建**（R24）。
    ///
    /// 后半句是这套筛选敢于默认挡掉 Notice 的全部前提：管理网页和客户端托盘上照样看得见，
    /// 它只是不再单独打扰人一次。真要一条都不漏的人，把「发送哪些告警」改成「全部都发」。
    /// </summary>
    [Fact]
    public async Task 默认筛选挡掉提示级的邮件但不挡告警本身()
    {
        await EnableEmailChannelAsync(minLevel: "warning");
        var noticeKey = $"test:{Guid.NewGuid():N}:notice-filtered";
        var warningKey = $"test:{Guid.NewGuid():N}:warning-passes";

        await using (var scope = _services.CreateAsyncScope())
        {
            var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
            await alerting.RaiseAsync(noticeKey, AlertLevel.Notice, "client_enrollment", "客户端自动登记成功");
            await alerting.RaiseAsync(warningKey, AlertLevel.Warning, "backup_missed", "到点没有备份");
        }

        Assert.Equal(0, await DeliveryCountAsync(noticeKey));
        Assert.Equal(1, await DeliveryCountAsync(warningKey));

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.Alerts.AnyAsync(a => a.AlertKey == noticeKey),
                "提示级告警本身必须照建：筛掉的是通知，不是告警");
        }
    }

    /// <summary>按类别排除：等级够也不发，而告警同样照建。</summary>
    [Fact]
    public async Task 被排除的类别不发邮件()
    {
        await EnableEmailChannelAsync(minLevel: "warning", excludedCategory: "client_resource");

        var excludedKey = $"test:{Guid.NewGuid():N}:cpu";
        var keptKey = $"test:{Guid.NewGuid():N}:missed";
        await using (var scope = _services.CreateAsyncScope())
        {
            var alerting = scope.ServiceProvider.GetRequiredService<IAlertingService>();
            await alerting.RaiseAsync(excludedKey, AlertLevel.Warning, "client_resource", "客户端 CPU 使用率过高");
            await alerting.RaiseAsync(keptKey, AlertLevel.Warning, "backup_missed", "到点没有备份");
        }

        Assert.Equal(0, await DeliveryCountAsync(excludedKey));
        Assert.Equal(1, await DeliveryCountAsync(keptKey));

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.Alerts.AnyAsync(a => a.AlertKey == excludedKey));
        }
    }

    // ---------- D9 ----------

    /// <summary>
    /// 告警要走自己的事务，不能借调用方那一个。
    ///
    /// 原先注入的是同作用域的 DbContext，RaiseAsync 结尾直接 SaveChangesAsync——
    /// 调用方在方法中途改过、还没打算提交的实体会被一起写进去。
    /// 这里用一个「改了实体但从不 SaveChanges」的调用方来钉住它：
    /// 告警要落库，而调用方那半成品的改动一个字节都不能进去。
    /// </summary>
    [Fact]
    public async Task 告警不会把调用方未提交的改动一起写进去()
    {
        var (_, taskId) = await SeedClientTaskAsync();
        var key = $"test:{Guid.NewGuid():N}:scope";

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 调用方改了任务名，还没走到自己的提交点
            var task = await db.BackupTasks.SingleAsync(t => t.Id == taskId);
            task.Name = "半成品-不该被写进去";

            await scope.ServiceProvider.GetRequiredService<IAlertingService>()
                .RaiseAsync(key, AlertLevel.Warning, "unit_test", "告警标题", taskId: taskId);

            // 刻意不 SaveChanges：调用方在这里因为别的原因放弃了这次修改
        }

        await using (var verify = _services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.True(await db.Alerts.AsNoTracking().AnyAsync(a => a.AlertKey == key));

            var task = await db.BackupTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
            Assert.NotEqual("半成品-不该被写进去", task.Name);
        }
    }

    // ---------- 基础设施 ----------

    /// <summary>下发一条上传指令并让它以失败收场，走 HandleCompletionSideEffects 那条告警路径。</summary>
    private async Task FailUploadCommandAsync(Guid clientId, Guid taskId)
    {
        Guid commandId;
        await using (var scope = _services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
            var command = await dispatcher.CreateCommandAsync(
                clientId, CommandType.UploadCandidate, taskId: taskId,
                // 每次一个全新的幂等键，模拟「指令被复位重发或新建」——
                // 正是这一点让原先按 commandId 去重的告警攒成一堆
                idempotencyKey: $"test-upload:{Guid.NewGuid():N}");
            commandId = command.Id;
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Commands.Where(c => c.Id == commandId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatus.Running));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAgentCommandService>()
                .ReportCompletedAsync(clientId, commandId, new CommandCompletedRequest
                {
                    Success = false,
                    ResultCode = "UPLOAD_FAILED",
                    ResultMessage = "网络中断"
                });
        }
    }

    private async Task<List<Core.Entities.Alert.NotificationDelivery>> DeliveriesAsync(string alertKey)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Alert!.AlertKey == alertKey)
            .OrderBy(d => d.Id)
            .ToListAsync();
    }

    private async Task<int> DeliveryCountAsync(string alertKey) => (await DeliveriesAsync(alertKey)).Count;

    /// <summary>
    /// 启用邮件渠道。<paramref name="minLevel"/> 默认 notice（全发）——
    /// 这一组测试考的是告警链本身（去重、升级、重发），不是投递筛选（R24 另有测试）。
    /// 不显式放开的话，默认筛选会挡掉 Notice，把两件事混在一个断言里。
    /// </summary>
    private async Task EnableEmailChannelAsync(string minLevel = "notice", string? excludedCategory = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = new Shared.Models.Admin.NotificationSettingsDto();
        settings.Email.Enabled = true;
        settings.Email.Recipients.Add("ops@example.com");
        settings.Email.Filter.MinLevel = minLevel;
        if (excludedCategory is not null)
            settings.Email.Filter.ExcludedCategories.Add(excludedCategory);
        var json = System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
            VALUES ({0}, CAST({1} AS jsonb), false, NULL)
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """, NotificationService.SettingsKey, json);
    }

    private async Task<(Guid ClientId, Guid TaskId)> SeedClientTaskAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"ac-{suffix}",
            Hostname = $"AC-{suffix[..8]}",
            DisplayName = $"告警链路客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"ac-task-{suffix[..8]}",
            ApplicationName = "AlertChainTest",
            SourcePath = @"D:\data",
            RecognizerType = RecognizerType.MultiFileSet,
            TaskMode = TaskMode.Automatic,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return (client.Id, task.Id);
    }

    private static IConfiguration SigningConfiguration()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:CommandSigningPrivateKey"] = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())
            })
            .Build();
    }
}
