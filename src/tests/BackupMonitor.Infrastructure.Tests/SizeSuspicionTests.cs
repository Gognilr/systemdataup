using System.Security.Cryptography;
using System.Text;
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
/// 「可疑」时收下并标记（R15）与历史基线大小判据（R16）。
///
/// 改之前：ValidateSizeThresholds 判出异常 → 候选的 precheck_status 停在非 passed →
/// UploadSessionService 抛 409、BatchOperationService 直接跳过。
/// 也就是「今天的备份只有平时 10% 大小」时，系统的反应是**一个字节都不收**。
/// 连带效果还有一条：被拒收的那天 last_success_at 不更新，过了宽限期
/// MissedBackupWorker 再报一条 backup_missed——同一件事报两条，
/// 而且其中一条把人引向错误方向（备份其实产生了，是被系统拒收了）。
///
/// **一份可疑的备份，比没有备份好。** 判断权交给人。
///
/// R16 那几条钉的是判据本身的三个要害：中位数、样本不足不判、文件数用宽带宽
/// （U8 附件库个数随启用年度不同，个数波动是常态，不能拿它当缺失判据）。
/// </summary>
[Collection("postgres")]
public class SizeSuspicionTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private ServiceProvider _services = null!;

    public SizeSuspicionTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

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
        sc.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        sc.AddScoped<IAgentPrecheckService, AgentPrecheckService>();
        sc.AddSingleton<ICommandDispatcher>(new RecordingCommandDispatcher());
        sc.AddSingleton<CommandSigner>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>
    /// R15 的验收：把 min_total_bytes 设成大于实际备份的值，
    /// 这一份仍然入库（预检通过、下发上传）、带可疑标记、有告警。
    /// </summary>
    [Fact]
    public async Task 低于固定下限时仍然通过预检只是被标记为可疑()
    {
        var (clientId, taskId) = await SeedClientTaskAsync(minTotalBytes: 1024 * 1024);

        var response = await SubmitAsync(clientId, taskId, "ZT001", sizeBytes: 4096);

        // 「拒收」的痕迹一个都不许剩下
        Assert.True(response.Accepted);
        Assert.NotEqual("rejected", response.NextAction);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.CandidateBackupSets.AsNoTracking()
            .FirstAsync(c => c.Id == response.CandidateBackupSetId);

        Assert.Equal(PrecheckStatus.Passed, candidate.PrecheckStatus);
        Assert.True(candidate.SizeSuspicious);
        Assert.Contains("低于下限", candidate.SizeSuspicionReason);

        // 告警照发——收下不等于不说话
        var alert = await db.Alerts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AlertKey == $"task:{taskId}:precheck:size_abnormal");
        Assert.NotNull(alert);
        Assert.Equal("size_abnormal", alert!.Category);
        // 正文必须写明「已经收下」，否则人会以为今天这份没进来而跑去手工补一次
        Assert.Contains("已照常收下", alert.Message);
    }

    /// <summary>大小正常时不该留下任何可疑标记——放宽处置不等于放弃判据。</summary>
    [Fact]
    public async Task 大小正常时不打可疑标记()
    {
        var (clientId, taskId) = await SeedClientTaskAsync(minTotalBytes: 1024);

        var response = await SubmitAsync(clientId, taskId, "ZT001", sizeBytes: 4096);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.CandidateBackupSets.AsNoTracking()
            .FirstAsync(c => c.Id == response.CandidateBackupSetId);

        Assert.False(candidate.SizeSuspicious);
        Assert.Null(candidate.SizeSuspicionReason);
    }

    /// <summary>
    /// R16 的验收：连续 10 次正常备份之后，人为造一次只有 10% 大小的备份能告警。
    /// 任务上一个固定阈值都没配——这正是绝大多数任务的真实状态，
    /// 也正是「固定死数抓不到最有价值的信号」这件事的落点。
    /// </summary>
    [Fact]
    public async Task 大小跌到历史中位数一成时判可疑()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var unitId = await SeedBusinessUnitAsync(taskId, "ZT001");
        await SeedHistoryAsync(clientId, taskId, unitId, count: 10, bytesEach: 1_000_000, filesEach: 20);

        var response = await SubmitAsync(clientId, taskId, "ZT001", sizeBytes: 100_000);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.CandidateBackupSets.AsNoTracking()
            .FirstAsync(c => c.Id == response.CandidateBackupSetId);

        Assert.True(candidate.SizeSuspicious);
        Assert.Contains("中位数", candidate.SizeSuspicionReason);
        // 仍然照收：R16 的判据变了，R15 的处置口径不能跟着倒回去
        Assert.Equal(PrecheckStatus.Passed, candidate.PrecheckStatus);
    }

    /// <summary>
    /// 样本不足就不判——新任务不该因为没有历史就报警
    /// （与漏备份巡检「刚建出来的任务不算漏」同一条原则）。
    /// </summary>
    [Fact]
    public async Task 历史样本不足时不判可疑()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var unitId = await SeedBusinessUnitAsync(taskId, "ZT001");
        await SeedHistoryAsync(clientId, taskId, unitId, count: 3, bytesEach: 1_000_000, filesEach: 20);

        var response = await SubmitAsync(clientId, taskId, "ZT001", sizeBytes: 100_000);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.CandidateBackupSets.AsNoTracking()
            .FirstAsync(c => c.Id == response.CandidateBackupSetId);

        Assert.False(candidate.SizeSuspicious);
    }

    /// <summary>
    /// U8 附件库个数随启用年度不同，个数波动是常态。
    /// 文件数掉到中位数的一半（20 → 10）不能报——那是每天都会发生的正常波动，
    /// 报出来的结果是人很快学会无视这条告警，真出事的那天也一样没人看。
    /// </summary>
    [Fact]
    public async Task 文件数正常波动不误报()
    {
        var (clientId, taskId) = await SeedClientTaskAsync();
        var unitId = await SeedBusinessUnitAsync(taskId, "ZT001");
        await SeedHistoryAsync(clientId, taskId, unitId, count: 10, bytesEach: 1_000_000, filesEach: 20);

        // 字节数保持正常（不触发字节判据），只让文件数掉一半
        var response = await SubmitAsync(clientId, taskId, "ZT001", sizeBytes: 1_000_000, fileCount: 10);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.CandidateBackupSets.AsNoTracking()
            .FirstAsync(c => c.Id == response.CandidateBackupSetId);

        Assert.False(candidate.SizeSuspicious);
    }

    // ---------- 基础设施 ----------

    private async Task<SubmitPrecheckResultResponse> SubmitAsync(
        Guid clientId, Guid taskId, string unit, long sizeBytes, int fileCount = 1)
    {
        await using var scope = _services.CreateAsyncScope();
        var precheck = scope.ServiceProvider.GetRequiredService<IAgentPrecheckService>();

        // 总字节数在文件之间平摊，余数给第一个文件——ValidatePassedResult 要求
        // TotalBytes 与清单逐字节对得上。
        var per = sizeBytes / fileCount;
        var files = Enumerable.Range(0, fileCount)
            .Select(i => new PrecheckFileDto
            {
                RelativePath = $"UFDATA-{i}.BAK",
                SizeBytes = i == 0 ? per + (sizeBytes % fileCount) : per,
                LastModifiedAt = FileTime,
                Sha256 = Hex(SHA256.HashData(Encoding.UTF8.GetBytes($"{unit}-{i}-{sizeBytes}")))
            })
            .ToList();
        var manifest = ManifestOf(files);

        return await precheck.SubmitResultAsync(clientId, taskId, new SubmitPrecheckResultRequest
        {
            CandidateKey = $"{taskId:N}:{unit}:{manifest}",
            SourceRoot = @"D:\自动备份\" + unit,
            PrecheckStatus = "passed",
            TotalFiles = files.Count,
            TotalBytes = files.Sum(f => f.SizeBytes),
            ManifestHash = manifest,
            QuickFingerprint = $"qf-{unit}-{sizeBytes}",
            BusinessUnit = new PrecheckBusinessUnitDto { ExternalKey = unit, DisplayName = unit },
            Files = files
        });
    }

    private async Task<Guid> SeedBusinessUnitAsync(Guid taskId, string externalKey)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var unit = new BusinessUnit
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            ExternalKey = externalKey,
            DisplayName = externalKey,
            LastDiscoveredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.BusinessUnits.Add(unit);
        await db.SaveChangesAsync();
        return unit.Id;
    }

    /// <summary>
    /// 造 N 份历史备份集作为基线样本。每份都要有一个自己的候选：
    /// backup_sets.source_candidate_id 是非空外键，而且 V033 的部分唯一索引
    /// 不允许两份「活着」的备份集指向同一个候选。
    /// </summary>
    private async Task SeedHistoryAsync(
        Guid clientId, Guid taskId, Guid unitId, int count, long bytesEach, int filesEach)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        for (var i = 0; i < count; i++)
        {
            var candidate = new CandidateBackupSet
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                TaskId = taskId,
                BusinessUnitId = unitId,
                CandidateKey = $"history:{taskId:N}:{i}:{Guid.NewGuid():N}",
                SourceRoot = @"D:\自动备份\ZT001",
                PrecheckStatus = PrecheckStatus.Passed,
                TotalFiles = filesEach,
                TotalBytes = bytesEach,
                DiscoveredAt = now.AddDays(-count + i),
                CreatedAt = now.AddDays(-count + i)
            };
            var session = new Core.Entities.Upload.UploadSession
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                TaskId = taskId,
                CandidateBackupSetId = candidate.Id,
                Status = UploadStatus.Committed,
                TotalFiles = filesEach,
                TotalBytes = bytesEach,
                StartedAt = now,
                LastActivityAt = now,
                CompletedAt = now,
                CommittedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.CandidateBackupSets.Add(candidate);
            db.UploadSessions.Add(session);
            db.BackupSets.Add(new BackupSet
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                TaskId = taskId,
                BusinessUnitId = unitId,
                SourceCandidateId = candidate.Id,
                UploadSessionId = session.Id,
                BackupSetCode = $"BS-HIST-{Guid.NewGuid():N}"[..24],
                Status = BackupSetStatus.Available,
                DiscoveredAt = candidate.DiscoveredAt,
                UploadedAt = now.AddDays(-count + i),
                TotalFiles = filesEach,
                TotalBytes = bytesEach,
                CreatedAt = candidate.CreatedAt
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>固定时间戳：清单哈希要可重现，不能跟着 DateTime.UtcNow 漂。</summary>
    private static readonly DateTime FileTime = new(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc);

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

    private async Task<(Guid ClientId, Guid TaskId)> SeedClientTaskAsync(long? minTotalBytes = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            MachineId = $"sus-{suffix}",
            Hostname = $"sus-host-{suffix[..8]}",
            DisplayName = $"可疑判定客户端-{suffix[..8]}",
            Status = ClientStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = $"sus-task-{suffix[..8]}",
            ApplicationName = "U8",
            SourcePath = @"D:\自动备份",
            RecognizerType = RecognizerType.SubdirectoryUnits,
            RecognizerConfig = "{}",
            TaskMode = TaskMode.Automatic,
            MinTotalBytes = minTotalBytes,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.BackupTasks.Add(task);
        await db.SaveChangesAsync();
        return (client.Id, task.Id);
    }

    /// <summary>CommandSigner 需要一把真实的 RSA 私钥才能构造。</summary>
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
