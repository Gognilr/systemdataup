using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// 「一个任务多个业务单元」的预检回归测试。
///
/// 现场表现（2026-08-29 瑞来 U8）：任务有两个账套 ZT001 与 ZT201-ZT216，
/// 界面上点「立即备份」，弹窗说扫描通过、已开始上传，而「传输中」页面自始至终是空的，
/// 服务端日志里一行「创建上传会话」都没有。三条缺陷叠在一起：
///
///   一、去重键只认 commandId：第 1 个账套写完就把闸关上，第 2 个账套的结果
///       被当成重复提交丢弃（日志里表现为 precheck-results 返回 200
///       却没有任何「预检结果已提交」）；
///   二、指令在第 1 个账套就被判终态：之后每一条进度上报都 409，
///       人再点一次「立即备份」会把这条还在跑的指令复位成 pending，
///       Agent 扫完上报完成时撞 409，这一轮的结果全丢；
///   三、那个 409 从 Agent 的兜底 catch 里抛出，把同一批认领的两条
///       upload_candidate 一起带走——上传指令就此人间蒸发（那条在 Agent 侧修）。
///
/// 这些都值得钉在测试里：它们的共同症状是「界面说备份了，实际一个字节都没传」，
/// 而这正是这个产品唯一不能出错的那件事。
/// </summary>
[Collection("postgres")]
public class MultiUnitPrecheckTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;
    private RecordingCommandDispatcher _dispatcher = null!;

    public MultiUnitPrecheckTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _dispatcher = new RecordingCommandDispatcher();

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(SigningConfiguration());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        sc.AddScoped<IAgentPrecheckService, AgentPrecheckService>();
        sc.AddSingleton<ICommandDispatcher>(_dispatcher);
        sc.AddSingleton<CommandSigner>();
        sc.AddScoped<CommandService>();
        sc.AddScoped<IAgentCommandService>(sp => sp.GetRequiredService<CommandService>());
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();

        // 这一组测的是「每个业务单元各自下发一条上传」，与全局上传名额无关。
        // 但名额是一条**全库共享**的系统配置，而并发那一组会把它压到 1 或 2 并留下活动会话；
        // 名额一满，自动下发就改成排队（TryDeferUploadAsync），下发计数变成 0，
        // 这一组就会随测试执行顺序时好时坏。显式把名额顶开，让它只测自己那件事。
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
            VALUES ({SequentialExecutionWorker.GlobalUploadLimitKey}, '64'::jsonb, false, NULL)
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            """);
        scope.ServiceProvider.GetRequiredService<SystemSettingsProvider>().Invalidate();
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task 同一条指令下每个业务单元都被受理并各自下发上传()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedPrecheckCommandAsync(clientId, taskId);

        var zt001 = await SubmitAsync(clientId, taskId, commandId, "ZT001", passed: true);
        var zt201 = await SubmitAsync(clientId, taskId, commandId, "ZT201-ZT216", passed: true);
        var zt900 = await SubmitAsync(clientId, taskId, commandId, "ZT900", passed: false);

        // 第 2、第 3 个单元不能再被当成「重复上报」丢掉
        Assert.NotEqual("duplicate_submission", zt201.NextAction);
        Assert.NotEqual("duplicate_submission", zt900.NextAction);
        Assert.NotEqual(zt001.CandidateBackupSetId, zt201.CandidateBackupSetId);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidates = await db.CandidateBackupSets.AsNoTracking()
            .Where(c => c.TaskId == taskId).ToListAsync();
        Assert.Equal(3, candidates.Count);

        // 通过的两个账套各下发一条上传，没通过的那个不发
        Assert.Equal(2, _dispatcher.Dispatched.Count(t => t == CommandType.UploadCandidate));

        // 指令仍在执行中：它的结论要等 Agent 把所有单元扫完再汇总
        var command = await db.Commands.AsNoTracking().FirstAsync(c => c.Id == commandId);
        Assert.Equal(CommandStatus.Running, command.Status);

        var entries = ParseCandidates(command.ResultPayload);
        Assert.Equal(3, entries.Count);
        Assert.Equal(2, entries.Count(e => e.GetProperty("status").GetString() == "passed"));
        Assert.All(
            entries.Where(e => e.GetProperty("status").GetString() == "passed"),
            e => Assert.Equal("dispatched", e.GetProperty("uploadState").GetString()));
    }

    [Fact]
    public async Task 同一个业务单元重复上报仍然幂等()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedPrecheckCommandAsync(clientId, taskId);

        var first = await SubmitAsync(clientId, taskId, commandId, "ZT001", passed: true);
        var again = await SubmitAsync(clientId, taskId, commandId, "ZT001", passed: true);

        Assert.Equal("duplicate_submission", again.NextAction);
        Assert.Equal(first.CandidateBackupSetId, again.CandidateBackupSetId);
        Assert.Equal(1, _dispatcher.Dispatched.Count(t => t == CommandType.UploadCandidate));
    }

    [Fact]
    public async Task 进度上报不会抹掉已经攒下的候选清单()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedPrecheckCommandAsync(clientId, taskId);
        await SubmitAsync(clientId, taskId, commandId, "ZT001", passed: true);

        await using (var scope = _services.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAgentCommandService>();
            // 第 1 个账套交完之后，Agent 还在扫第 2 个账套，进度照报——
            // 原先这一下会把整块 result_payload 覆盖掉，候选清单一起没了。
            await commands.ReportProgressAsync(clientId, commandId, new CommandProgressRequest
            {
                Percent = 50m,
                Stage = "hashing",
                Message = "第 2/2 个：ZT201-ZT216 · 正在校验 UFDATA.BAK"
            });
        }

        await using var check = _services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var payload = (await db.Commands.AsNoTracking().FirstAsync(c => c.Id == commandId)).ResultPayload;

        Assert.Single(ParseCandidates(payload));
        using var doc = JsonDocument.Parse(payload!);
        Assert.True(doc.RootElement.TryGetProperty("progress", out _));
    }

    [Fact]
    public async Task 指令结论由全部单元汇总而不是第一个()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedPrecheckCommandAsync(clientId, taskId);

        // 第 1 个账套没有新备份，第 2 个有——原先第 1 个就把整条指令判成 failed，
        // 界面据此说「没有发现新的备份文件」，第 2 个账套的备份一个字节都不会传。
        await SubmitAsync(clientId, taskId, commandId, "ZT001", passed: false);
        await SubmitAsync(clientId, taskId, commandId, "ZT201-ZT216", passed: true);

        await CompleteAsync(clientId, commandId);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var command = await db.Commands.AsNoTracking().FirstAsync(c => c.Id == commandId);

        Assert.Equal(CommandStatus.Succeeded, command.Status);
        Assert.Equal("passed", command.ResultCode);
        Assert.Equal(2, ParseCandidates(command.ResultPayload).Count);   // 清单不能被完成上报抹掉
    }

    /// <summary>
    /// 全部单元都没有新备份 = 正常结束，不是失败。
    ///
    /// 这一条原先断言的是 Failed——名字说「不当成故障」，断言却把指令判成失败，
    /// 自相矛盾的地方就是缺陷本身。现场（2026-09-04 15:06 瑞来_大宗物料）的表现是
    /// 执行记录里写着「指令failed：扫描完成：1 个业务单元，0 个有新备份」：
    /// 扫描完成了，也没有任何东西出错，而每天例行的这个结果一律显示成失败，
    /// 人于是分不出哪一次是真出了事。
    ///
    /// 附带的代价更隐蔽：指令一旦是终态失败，SequentialExecutionWorker 的判定
    /// 在看清单之前就走了「指令死了」那条分支，ClassifyPrecheckAsync 里
    /// 「全部单元都没有新备份 → 成功，写『这次没有新备份』」那条路永远到不了。
    /// </summary>
    [Fact]
    public async Task 全部单元都没有新备份时算正常结束而不是失败()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedPrecheckCommandAsync(clientId, taskId);

        await SubmitAsync(clientId, taskId, commandId, "ZT001", passed: false);
        await SubmitAsync(clientId, taskId, commandId, "ZT201-ZT216", passed: false);
        await CompleteAsync(clientId, commandId);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var command = await db.Commands.AsNoTracking().FirstAsync(c => c.Id == commandId);
        Assert.Equal(CommandStatus.Succeeded, command.Status);
        // 结论仍然如实写着「这次什么都没有」，成功指的是这次扫描本身没出事
        Assert.Equal("no_new_backup", command.ResultCode);
        Assert.Equal("扫描完成：2 个业务单元，0 个有新备份", command.ResultMessage);

        // 「今天还没产生新备份」是最常见的正常结局，报成故障等于每天造一批假告警
        var raised = await db.Alerts.AsNoTracking()
            .CountAsync(a => a.AlertKey == $"command:{commandId}:failed");
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// 真的没通过预检（大小异常之类）才判指令失败——把这条一起钉住，
    /// 否则上面那条修完之后很容易滑向「预检永远成功」，那是更糟的谎话。
    /// </summary>
    [Fact]
    public async Task 全部单元都没通过预检时仍然判指令失败()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedPrecheckCommandAsync(clientId, taskId);

        await SubmitAsync(clientId, taskId, commandId, "ZT001", passed: false, status: "size_abnormal");
        await SubmitAsync(clientId, taskId, commandId, "ZT201-ZT216", passed: false, status: "size_abnormal");
        await CompleteAsync(clientId, commandId);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var command = await db.Commands.AsNoTracking().FirstAsync(c => c.Id == commandId);

        Assert.Equal(CommandStatus.Failed, command.Status);
        Assert.Equal("size_abnormal", command.ResultCode);
        Assert.Contains("2 个没通过预检", command.ResultMessage);
    }

    /// <summary>
    /// 一部分单元没通过、另一部分正常时，整条指令不算失败：
    /// 判失败会让执行记录走「指令死了」那条分支，把另外那些单元的上传结论一并埋掉。
    /// 没通过的那几个由 size_abnormal / precheck_failed 告警各自报出去。
    /// </summary>
    [Fact]
    public async Task 部分单元没通过时指令不判失败()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var commandId = await SeedPrecheckCommandAsync(clientId, taskId);

        await SubmitAsync(clientId, taskId, commandId, "ZT001", passed: false, status: "size_abnormal");
        await SubmitAsync(clientId, taskId, commandId, "ZT201-ZT216", passed: true);
        await CompleteAsync(clientId, commandId);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var command = await db.Commands.AsNoTracking().FirstAsync(c => c.Id == commandId);

        Assert.Equal(CommandStatus.Succeeded, command.Status);
        Assert.Equal("passed", command.ResultCode);
        Assert.Contains("1 个没通过预检", command.ResultMessage);
    }

    // ---------- 基础设施 ----------

    /// <summary><paramref name="status"/> 只在 <paramref name="passed"/> 为 false 时生效，默认 no_new_backup。</summary>
    private async Task<SubmitPrecheckResultResponse> SubmitAsync(
        Guid clientId, Guid taskId, Guid commandId, string unit, bool passed, string status = "no_new_backup")
    {
        await using var scope = _services.CreateAsyncScope();
        var precheck = scope.ServiceProvider.GetRequiredService<IAgentPrecheckService>();
        var root = @"D:\自动备份\" + unit;

        if (!passed)
        {
            return await precheck.SubmitResultAsync(clientId, taskId, new SubmitPrecheckResultRequest
            {
                CommandId = commandId,
                CandidateKey = $"{taskId:N}:{unit}:{status}:{root}",
                SourceRoot = root,
                PrecheckStatus = status,
                FailureCode = status.ToUpperInvariant(),
                FailureMessage = status == "no_new_backup"
                    ? "快速指纹与上一次候选一致，未发现新的备份"
                    : "备份大小超出设定范围",
                BusinessUnit = new PrecheckBusinessUnitDto { ExternalKey = unit, DisplayName = unit }
            });
        }

        var files = new List<PrecheckFileDto>
        {
            new()
            {
                RelativePath = "UFDATA.BAK",
                SizeBytes = 4096,
                LastModifiedAt = FileTime,
                Sha256 = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(unit)))
            }
        };
        var manifest = ManifestOf(files);

        return await precheck.SubmitResultAsync(clientId, taskId, new SubmitPrecheckResultRequest
        {
            CommandId = commandId,
            CandidateKey = $"{taskId:N}:{unit}:{manifest}",
            SourceRoot = root,
            PrecheckStatus = "passed",
            TotalFiles = files.Count,
            TotalBytes = files.Sum(f => f.SizeBytes),
            ManifestHash = manifest,
            QuickFingerprint = $"qf-{unit}",
            BusinessUnit = new PrecheckBusinessUnitDto { ExternalKey = unit, DisplayName = unit },
            Files = files
        });
    }

    /// <summary>Agent 扫完全部单元之后的那一次完成上报：结果体是空的，结论靠服务端汇总。</summary>
    private async Task CompleteAsync(Guid clientId, Guid commandId)
    {
        await using var scope = _services.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IAgentCommandService>();
        await commands.ReportCompletedAsync(clientId, commandId, new CommandCompletedRequest
        {
            Success = true,
            ResultCode = "PRECHECK_SUBMITTED",
            ResultMessage = "已提交 2 个预检结果"
        });
    }

    private static List<JsonElement> ParseCandidates(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return [];
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.TryGetProperty("candidates", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
    }

    /// <summary>固定时间戳：清单哈希要可重现，不能跟着 DateTime.UtcNow 漂。</summary>
    private static readonly DateTime FileTime = new(2026, 8, 29, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>与 AgentPrecheckService.ValidatePassedResult 同一套算法，否则服务端会判清单不一致。</summary>
    private static string ManifestOf(List<PrecheckFileDto> files)
    {
        var manifest = string.Join('\n', files
            .Select(f => new
            {
                RelativePath = f.RelativePath.Replace('\\', '/'),
                f.SizeBytes,
                f.LastModifiedAt,
                Sha256 = f.Sha256!.ToLowerInvariant()
            })
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{f.RelativePath}|{f.SizeBytes}|{f.LastModifiedAt.Ticks}|{f.Sha256}"));
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private async Task<Guid> SeedPrecheckCommandAsync(Guid clientId, Guid taskId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var signer = scope.ServiceProvider.GetRequiredService<CommandSigner>();

        var command = new Command
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TaskId = taskId,
            CommandType = CommandType.PrecheckTask,
            Status = CommandStatus.Running,
            Priority = 100,
            Nonce = Guid.NewGuid().ToString("N"),
            ClaimedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            IdempotencyKey = $"manual-precheck:{taskId:N}"
        };
        command.Signature = signer.SignCommand(command);
        db.Commands.Add(command);
        await db.SaveChangesAsync();
        return command.Id;
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
            MachineId = $"mup-{suffix}",
            Hostname = $"mup-host-{suffix[..8]}",
            DisplayName = $"多账套客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"mup-task-{suffix[..8]}",
            ApplicationName = "U8",
            SourcePath = @"D:\自动备份",
            RecognizerType = RecognizerType.SubdirectoryUnits,
            RecognizerConfig = "{}",
            TaskMode = TaskMode.Automatic,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return (client.Id, task.Id);
    }

    /// <summary>CommandSigner 需要一把真实的 RSA 私钥才能构造</summary>
    private static IConfiguration SigningConfiguration()
    {
        using var rsa = RSA.Create(2048);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:CommandSigningPrivateKey"] = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())
            })
            .Build();
    }
}
