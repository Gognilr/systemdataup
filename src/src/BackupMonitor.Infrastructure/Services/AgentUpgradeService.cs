using System.Text.Json;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// Agent 升级服务（整改清单 2026-09-10 · R20 重写）。
///
/// 重写之前它只做一件事：给每台机器发一条 upgrade_agent 指令，然后就结束了。
/// 界面显示「下发成功」、Agent 回报「UPGRADE_STAGED 成功」，而机器上运行的
/// 程序一个字节没变——那句成功说的是「包解压好了」。
///
/// 现在这个服务负责一整条有始有终的流程：
/// - **分批（金丝雀）**：先 1 台，等它心跳回来且版本号确实变了，再放 5 台，再铺开。
/// - **成功的判据是版本号**：客户端心跳自报的 agent_version 变成目标版本才算成功；
///   指令回报只是过程信号，拿它当成功正是这个缺陷的形状。
/// - **一批失败就刹车**：后面的批次不再推。升级失败的机器连不上服务端，
///   连修复指令都收不到，只能上门——29 台一起推等于 29 次上门。
/// </summary>
public interface IAgentUpgradeService
{
    /// <summary>创建一次分批升级下发并放出第一批（逐项返回第一批的下发结果）</summary>
    Task<UpgradeAgentResponseDto> DispatchAsync(UpgradeAgentRequestDto request, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>升级下发列表（新的在前）</summary>
    Task<List<AgentUpgradeSummaryDto>> ListAsync(int limit, CancellationToken ct = default);

    /// <summary>单次下发的逐机器详情</summary>
    Task<AgentUpgradeDetailDto> GetAsync(Guid upgradeId, CancellationToken ct = default);

    /// <summary>取消尚未下发的批次（已经发下去的那批拦不住，只是不再往后推）</summary>
    Task<AgentUpgradeDetailDto> CancelAsync(Guid upgradeId, CancellationToken ct = default);

    /// <summary>下发表单的自动填充信息（随附版本 / 下载地址 / SHA-256）</summary>
    Task<AgentUpgradePackageInfoDto> GetPackageInfoAsync(string? requestBaseUrl, CancellationToken ct = default);

    /// <summary>Agent 升级完成（或回滚）后的主动回报</summary>
    Task ReportResultAsync(Guid clientId, AgentUpgradeResultRequest request, CancellationToken ct = default);

    /// <summary>推进一次下发：结算当前批次，必要时放出下一批（由巡检工作器调用）</summary>
    Task AdvanceAsync(Guid upgradeId, CancellationToken ct = default);
}

/// <summary>Agent 升级实现</summary>
public class AgentUpgradeService : IAgentUpgradeService
{
    /// <summary>单台机器从下发到「心跳回来且版本号变了」的等待上限（分钟）。</summary>
    public const string TimeoutMinutesKey = "agent_upgrade_timeout_minutes";

    public const int DefaultTimeoutMinutes = 30;

    /// <summary>默认分批计划：先 1 台，再 5 台，再全部。</summary>
    public const string DefaultBatchPlan = "1,5,0";

    /// <summary>升级包在服务端自己的下载路径，与 build-turnkey.ps1 写出的位置一致。</summary>
    public const string PackageRelativeUrl = "downloads/BackupMonitor.Agent.zip";

    private const string PackageHashFileRelativePath = "wwwroot/downloads/BackupMonitor.Agent.zip.sha256";

    private const string VersionFileRelativePath = "wwwroot/downloads/agent-version.txt";

    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly SystemSettingsProvider _settings;
    private readonly IAlertingService _alerting;
    private readonly ILogger<AgentUpgradeService> _logger;

    public AgentUpgradeService(
        AppDbContext db,
        ICommandDispatcher dispatcher,
        ICurrentContext context,
        IAuditRecorder audit,
        SystemSettingsProvider settings,
        IAlertingService alerting,
        ILogger<AgentUpgradeService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _context = context;
        _audit = audit;
        _settings = settings;
        _alerting = alerting;
        _logger = logger;
    }

    public async Task<UpgradeAgentResponseDto> DispatchAsync(UpgradeAgentRequestDto request, string? idempotencyKey, CancellationToken ct = default)
    {
        if (request.ClientIds.Count == 0)
            throw new ValidationFailedException("clientIds 至少需要一台客户端");
        if (string.IsNullOrWhiteSpace(request.TargetVersion))
            throw new ValidationFailedException("targetVersion 必填");
        if (string.IsNullOrWhiteSpace(request.PackageUrl))
            throw new ValidationFailedException("packageUrl 必填");
        if (!Uri.TryCreate(request.PackageUrl.Trim(), UriKind.Absolute, out var packageUri)
            || (!string.Equals(packageUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(packageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationFailedException("packageUrl 必须是 HTTP 或 HTTPS 地址");
        }
        if (string.IsNullOrWhiteSpace(request.PackageSha256)
            || !System.Text.RegularExpressions.Regex.IsMatch(request.PackageSha256, "^[0-9a-fA-F]{64}$"))
        {
            throw new ValidationFailedException("packageSha256 必须为 64 位十六进制");
        }

        var response = new UpgradeAgentResponseDto();
        var clientIds = request.ClientIds.Distinct().ToList();
        var clients = await _db.Clients
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        // 先把不能下发的机器摘掉，再排批次：把一台「已禁用」的机器排进第一批，
        // 等于金丝雀那一批从头就不可能成功，后面的批次永远等不到放行。
        var eligible = new List<Guid>();
        foreach (var clientId in clientIds)
        {
            var item = new UpgradeClientResultDto { ClientId = clientId };
            if (!clients.TryGetValue(clientId, out var client))
            {
                item.ErrorCode = "CLIENT_NOT_REGISTERED";
                item.ErrorMessage = "客户端不存在";
                response.Failed++;
                response.Items.Add(item);
                continue;
            }

            item.Hostname = client.Hostname;
            if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            {
                item.ErrorCode = "CLIENT_DISABLED";
                item.ErrorMessage = $"客户端状态为 {EnumMapping.ToSnakeCase(client.Status)}，不能下发升级";
                response.Failed++;
                response.Items.Add(item);
                continue;
            }

            eligible.Add(clientId);
        }

        if (eligible.Count == 0)
            throw new BusinessException("UPGRADE_NO_ELIGIBLE_CLIENT", "选中的客户端没有一台可以下发升级", 400);

        var batchPlan = NormalizeBatchPlan(request.BatchPlan);
        var upgrade = new AgentUpgrade
        {
            Id = Guid.NewGuid(),
            TargetVersion = request.TargetVersion.Trim(),
            PackageUrl = packageUri.ToString(),
            PackageSha256 = request.PackageSha256.ToLowerInvariant(),
            Note = request.Note,
            Status = AgentUpgradeStatus.Pending,
            BatchPlan = batchPlan,
            CurrentBatch = -1,
            CreatedBy = _context.UserId,
            CreatedByName = _context.Username,
            CreatedAt = DateTime.UtcNow
        };
        _db.AgentUpgrades.Add(upgrade);

        var sizes = ResolveBatchSizes(batchPlan, eligible.Count);
        var index = 0;
        for (var batch = 0; batch < sizes.Count; batch++)
        {
            for (var i = 0; i < sizes[batch] && index < eligible.Count; i++, index++)
            {
                var clientId = eligible[index];
                _db.AgentUpgradeTargets.Add(new AgentUpgradeTarget
                {
                    Id = Guid.NewGuid(),
                    UpgradeId = upgrade.Id,
                    ClientId = clientId,
                    BatchIndex = batch,
                    Status = AgentUpgradeTargetStatus.Waiting,
                    VersionBefore = clients[clientId].AgentVersion
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        // 第一批就地放出去：管理员点了下发就该立刻看到第一台动起来，
        // 而不是等巡检工作器下一轮才开始——那段等待会被当成「没反应」再点一次。
        var dispatched = await DispatchBatchAsync(upgrade, 0, idempotencyKey, ct);
        foreach (var target in dispatched)
        {
            var item = new UpgradeClientResultDto
            {
                ClientId = target.ClientId,
                Hostname = clients[target.ClientId].Hostname,
                Success = target.Status == AgentUpgradeTargetStatus.Dispatched,
                CommandId = target.CommandId,
                ErrorCode = target.ErrorCode,
                ErrorMessage = target.ErrorMessage
            };

            if (item.Success)
                response.Dispatched++;
            else
                response.Failed++;

            response.Items.Add(item);
        }

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("agent.upgrade_dispatch", AuditResult.Success, "agent_upgrade", upgrade.Id,
            afterData: JsonSerializer.Serialize(new
            {
                upgradeId = upgrade.Id,
                clientCount = eligible.Count,
                batchPlan,
                firstBatch = response.Dispatched,
                request.TargetVersion,
                request.PackageUrl
            }), ct: ct);

        _logger.LogInformation(
            "Agent 升级下发已创建 {UpgradeId}：目标版本 {Version}，共 {Total} 台，分批 {Plan}，第一批下发 {Dispatched} 台",
            upgrade.Id, upgrade.TargetVersion, eligible.Count, batchPlan, response.Dispatched);

        return response;
    }

    public async Task<List<AgentUpgradeSummaryDto>> ListAsync(int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var upgrades = await _db.AgentUpgrades.AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        var ids = upgrades.Select(u => u.Id).ToList();
        var targets = await _db.AgentUpgradeTargets.AsNoTracking()
            .Where(t => ids.Contains(t.UpgradeId))
            .Select(t => new { t.UpgradeId, t.Status })
            .ToListAsync(ct);

        return upgrades
            .Select(u => ToSummary(u, targets.Where(t => t.UpgradeId == u.Id).Select(t => t.Status)))
            .ToList();
    }

    public async Task<AgentUpgradeDetailDto> GetAsync(Guid upgradeId, CancellationToken ct = default)
    {
        var upgrade = await _db.AgentUpgrades.AsNoTracking().FirstOrDefaultAsync(u => u.Id == upgradeId, ct)
            ?? throw new BusinessException("AGENT_UPGRADE_NOT_FOUND", "升级下发记录不存在", 404);

        var rows = await _db.AgentUpgradeTargets.AsNoTracking()
            .Where(t => t.UpgradeId == upgradeId)
            .Join(_db.Clients.AsNoTracking(), t => t.ClientId, c => c.Id, (t, c) => new { Target = t, c.Hostname, c.DisplayName })
            .ToListAsync(ct);

        var summary = ToSummary(upgrade, rows.Select(r => r.Target.Status));
        return new AgentUpgradeDetailDto
        {
            Id = summary.Id,
            TargetVersion = summary.TargetVersion,
            PackageUrl = summary.PackageUrl,
            Note = summary.Note,
            Status = summary.Status,
            BatchPlan = summary.BatchPlan,
            CurrentBatch = summary.CurrentBatch,
            TotalCount = summary.TotalCount,
            SucceededCount = summary.SucceededCount,
            FailedCount = summary.FailedCount,
            PendingCount = summary.PendingCount,
            CreatedByName = summary.CreatedByName,
            CreatedAt = summary.CreatedAt,
            CompletedAt = summary.CompletedAt,
            FailureReason = summary.FailureReason,
            Targets = rows
                .OrderBy(r => r.Target.BatchIndex)
                .ThenBy(r => r.Hostname, StringComparer.OrdinalIgnoreCase)
                .Select(r => new AgentUpgradeTargetDto
                {
                    ClientId = r.Target.ClientId,
                    Hostname = r.Hostname,
                    DisplayName = r.DisplayName,
                    BatchIndex = r.Target.BatchIndex,
                    Status = EnumMapping.ToSnakeCase(r.Target.Status),
                    CommandId = r.Target.CommandId,
                    VersionBefore = r.Target.VersionBefore,
                    ReportedVersion = r.Target.ReportedVersion,
                    DispatchedAt = r.Target.DispatchedAt,
                    CompletedAt = r.Target.CompletedAt,
                    ErrorCode = r.Target.ErrorCode,
                    ErrorMessage = r.Target.ErrorMessage
                })
                .ToList()
        };
    }

    public async Task<AgentUpgradeDetailDto> CancelAsync(Guid upgradeId, CancellationToken ct = default)
    {
        var upgrade = await _db.AgentUpgrades.FirstOrDefaultAsync(u => u.Id == upgradeId, ct)
            ?? throw new BusinessException("AGENT_UPGRADE_NOT_FOUND", "升级下发记录不存在", 404);

        if (upgrade.Status is AgentUpgradeStatus.Succeeded or AgentUpgradeStatus.Failed or AgentUpgradeStatus.Cancelled)
            throw new BusinessException("AGENT_UPGRADE_NOT_RUNNING", "这次升级下发已经结束，无法取消", 400);

        // 只拦还没发出去的。已经发下去的指令拦不住——包可能已经在下载，
        // 甚至已经在换文件了，这时候「取消」只会变成一个骗人的按钮。
        var waiting = await _db.AgentUpgradeTargets
            .Where(t => t.UpgradeId == upgradeId && t.Status == AgentUpgradeTargetStatus.Waiting)
            .ToListAsync(ct);
        foreach (var target in waiting)
        {
            target.Status = AgentUpgradeTargetStatus.Cancelled;
            target.CompletedAt = DateTime.UtcNow;
            target.ErrorCode = "UPGRADE_CANCELLED";
            target.ErrorMessage = "管理员取消了后续批次";
        }

        upgrade.Status = AgentUpgradeStatus.Cancelled;
        upgrade.CompletedAt = DateTime.UtcNow;
        upgrade.FailureReason = "管理员取消";
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("agent.upgrade_cancel", AuditResult.Success, "agent_upgrade", upgradeId, ct: ct);
        return await GetAsync(upgradeId, ct);
    }

    public async Task<AgentUpgradePackageInfoDto> GetPackageInfoAsync(string? requestBaseUrl, CancellationToken ct = default)
    {
        var result = new AgentUpgradePackageInfoDto();
        var missing = new List<string>();

        try
        {
            var versionPath = ResolveContentPath(VersionFileRelativePath);
            if (File.Exists(versionPath))
                result.Version = StripBuildSuffix((await File.ReadAllTextAsync(versionPath, ct)).Trim());
            else
                missing.Add("随附版本号文件");

            var hashPath = ResolveContentPath(PackageHashFileRelativePath);
            if (File.Exists(hashPath))
            {
                // build-turnkey.ps1 写出的是纯哈希；这里只取第一段，
                // 万一以后换成 sha256sum 那种「哈希 空格 文件名」的格式也不会读错。
                var text = (await File.ReadAllTextAsync(hashPath, ct)).Trim();
                var token = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (token is not null && System.Text.RegularExpressions.Regex.IsMatch(token, "^[0-9a-fA-F]{64}$"))
                    result.PackageSha256 = token.ToLowerInvariant();
                else
                    missing.Add("升级包 SHA-256 文件内容不是 64 位十六进制");
            }
            else
            {
                missing.Add("升级包 SHA-256 文件");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取随附升级包信息失败");
            missing.Add($"读取失败：{ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(requestBaseUrl))
            result.PackageUrl = $"{requestBaseUrl.TrimEnd('/')}/{PackageRelativeUrl}";

        if (missing.Count > 0)
            result.Unavailable = $"以下信息读不到，需要手工填写：{string.Join("、", missing)}";

        return result;
    }

    public async Task ReportResultAsync(Guid clientId, AgentUpgradeResultRequest request, CancellationToken ct = default)
    {
        // 先按指令 ID 认领；找不到再回落到「这台机器最近一次还没结束的升级」。
        // 回落是必要的：真正换文件的那一刻旧进程已经没了，指令 ID 只能靠 updater
        // 从暂存记录里带过来，而现场总有它带不过来的时候（暂存文件被清理等）。
        var targets = await _db.AgentUpgradeTargets
            .Where(t => t.ClientId == clientId
                        && (t.Status == AgentUpgradeTargetStatus.Dispatched || t.Status == AgentUpgradeTargetStatus.Waiting))
            .OrderByDescending(t => t.DispatchedAt)
            .ToListAsync(ct);

        var target = (request.CommandId is not null
                         ? targets.FirstOrDefault(t => t.CommandId == request.CommandId)
                         : null)
                     ?? targets.FirstOrDefault();
        if (target is null)
        {
            _logger.LogInformation(
                "客户端 {ClientId} 回报了升级结果 {Status}，但服务端没有对应的在途下发记录，忽略", clientId, request.Status);
            return;
        }

        var succeeded = string.Equals(request.Status, "succeeded", StringComparison.OrdinalIgnoreCase);
        target.ReportedVersion = Trim(request.RunningVersion, 64);
        target.CompletedAt = DateTime.UtcNow;

        if (succeeded)
        {
            target.Status = AgentUpgradeTargetStatus.Succeeded;
            target.ErrorCode = null;
            target.ErrorMessage = null;
        }
        else
        {
            target.Status = AgentUpgradeTargetStatus.Failed;
            target.ErrorCode = Trim(request.Status, 64);
            target.ErrorMessage = Trim(request.Message, 2000);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "客户端 {ClientId} 回报升级结果 {Status}，运行版本 {Version}", clientId, request.Status, request.RunningVersion);
    }

    /// <summary>
    /// 推进一次下发：结算当前批次，必要时放出下一批。
    ///
    /// 结算的判据有两条，缺一不可：
    /// 1. 这台机器的心跳在下发之后回来过——**升级最危险的失败形态是它再也不上线**；
    /// 2. 它自报的 agent_version 已经是目标版本。
    /// </summary>
    public async Task AdvanceAsync(Guid upgradeId, CancellationToken ct = default)
    {
        var upgrade = await _db.AgentUpgrades.FirstOrDefaultAsync(u => u.Id == upgradeId, ct);
        if (upgrade is null || upgrade.Status is AgentUpgradeStatus.Succeeded
            or AgentUpgradeStatus.Failed or AgentUpgradeStatus.Cancelled)
            return;

        var timeoutMinutes = Math.Clamp(
            await _settings.GetIntAsync(TimeoutMinutesKey, DefaultTimeoutMinutes, ct), 5, 1440);

        var targets = await _db.AgentUpgradeTargets
            .Where(t => t.UpgradeId == upgradeId)
            .ToListAsync(ct);

        var clientIds = targets.Select(t => t.ClientId).ToList();
        var clients = await _db.Clients.AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Hostname, c.AgentVersion, c.LastHeartbeatAt })
            .ToDictionaryAsync(c => c.Id, ct);

        var now = DateTime.UtcNow;
        foreach (var target in targets.Where(t => t.Status == AgentUpgradeTargetStatus.Dispatched))
        {
            if (!clients.TryGetValue(target.ClientId, out var client))
                continue;

            var heartbeatBack = client.LastHeartbeatAt is not null
                                && target.DispatchedAt is not null
                                && client.LastHeartbeatAt > target.DispatchedAt;

            // 这台机器下发前就已经是目标版本时，版本号什么都证明不了——
            // 它从头到尾没变过，「等它变成目标版本」这条判据自动成立，
            // 于是一次什么都没换的升级会被判成功。**那正是 R20 要消灭的假成功本身。**
            // 这种情况下只认新进程自己的回报（ReportResultAsync 会直接把行改成 succeeded），
            // 等不到就按超时失败。版本号没递增的构建本来就不该当成一次升级发出去。
            var versionCanTell = !VersionMatches(target.VersionBefore, upgrade.TargetVersion);
            if (heartbeatBack && versionCanTell && VersionMatches(client.AgentVersion, upgrade.TargetVersion))
            {
                target.Status = AgentUpgradeTargetStatus.Succeeded;
                target.ReportedVersion = client.AgentVersion;
                target.CompletedAt = now;
                continue;
            }

            if (target.DispatchedAt is not null && (now - target.DispatchedAt.Value).TotalMinutes > timeoutMinutes)
            {
                target.Status = AgentUpgradeTargetStatus.Failed;
                target.CompletedAt = now;
                target.ErrorCode = "UPGRADE_TIMEOUT";
                // 两种完全不同的故障在这里分开写清楚：没心跳 = 这台机器可能真的起不来了（要上门）；
                // 有心跳但版本没变 = 程序还活着，只是没换过去（远程还能救）。
                target.ErrorMessage = heartbeatBack
                    ? versionCanTell
                        ? $"{timeoutMinutes} 分钟内心跳已恢复但版本号仍是 {client.AgentVersion ?? "未知"}，未切换到 {upgrade.TargetVersion}"
                        : $"这台机器下发前就已经是 {upgrade.TargetVersion}，版本号无法证明换过；"
                          + $"{timeoutMinutes} 分钟内也没等到客户端回报升级结果"
                    : $"{timeoutMinutes} 分钟内没有等到这台机器的心跳，升级后可能没起来";
            }
        }

        var currentBatch = upgrade.CurrentBatch;
        var currentBatchTargets = targets.Where(t => t.BatchIndex == currentBatch).ToList();
        var stillRunning = currentBatchTargets.Any(t =>
            t.Status is AgentUpgradeTargetStatus.Dispatched or AgentUpgradeTargetStatus.Waiting);

        if (stillRunning)
        {
            await _db.SaveChangesAsync(ct);
            return;
        }

        var failed = currentBatchTargets.Where(t => t.Status == AgentUpgradeTargetStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            // 分批的全部意义就在这一步刹车上。
            upgrade.Status = AgentUpgradeStatus.Failed;
            upgrade.CompletedAt = now;
            upgrade.FailureReason =
                $"第 {currentBatch + 1} 批有 {failed.Count} 台没能切换到 {upgrade.TargetVersion}，后续批次已停止";

            foreach (var waiting in targets.Where(t => t.Status == AgentUpgradeTargetStatus.Waiting))
            {
                waiting.Status = AgentUpgradeTargetStatus.Cancelled;
                waiting.CompletedAt = now;
                waiting.ErrorCode = "UPGRADE_BATCH_STOPPED";
                waiting.ErrorMessage = "前一批升级失败，本批未下发";
            }

            await _db.SaveChangesAsync(ct);
            await _alerting.RaiseAsync(
                $"agent_upgrade_failed:{upgrade.Id}",
                AlertLevel.Critical,
                "system",
                $"客户端升级下发已停止（目标版本 {upgrade.TargetVersion}）",
                upgrade.FailureReason + "。失败的机器："
                    + string.Join("、", failed.Select(f => clients.GetValueOrDefault(f.ClientId)?.Hostname ?? f.ClientId.ToString())),
                ct: ct);
            _logger.LogWarning("升级下发 {UpgradeId} 第 {Batch} 批失败，已停止后续批次", upgrade.Id, currentBatch + 1);
            return;
        }

        var nextBatch = currentBatch + 1;
        if (!targets.Any(t => t.BatchIndex == nextBatch))
        {
            upgrade.Status = AgentUpgradeStatus.Succeeded;
            upgrade.CompletedAt = now;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("升级下发 {UpgradeId} 全部完成，目标版本 {Version}", upgrade.Id, upgrade.TargetVersion);
            return;
        }

        await _db.SaveChangesAsync(ct);
        await DispatchBatchAsync(upgrade, nextBatch, idempotencyKey: null, ct);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("升级下发 {UpgradeId} 第 {Batch} 批已放出", upgrade.Id, nextBatch + 1);
    }

    /// <summary>把某一批的 upgrade_agent 指令发下去，并把批次指针推到这一批。</summary>
    private async Task<List<AgentUpgradeTarget>> DispatchBatchAsync(
        AgentUpgrade upgrade, int batchIndex, string? idempotencyKey, CancellationToken ct)
    {
        var targets = await _db.AgentUpgradeTargets
            .Where(t => t.UpgradeId == upgrade.Id && t.BatchIndex == batchIndex
                        && t.Status == AgentUpgradeTargetStatus.Waiting)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var target in targets)
        {
            try
            {
                var payload = new
                {
                    targetVersion = upgrade.TargetVersion,
                    packageUrl = upgrade.PackageUrl,
                    sha256 = upgrade.PackageSha256,
                    note = upgrade.Note
                };

                // 幂等键按客户端派生，保证逐客户端幂等（设计书 8.5 下发指令需幂等）
                var derivedKey = string.IsNullOrWhiteSpace(idempotencyKey)
                    ? null
                    : $"upgrade:{idempotencyKey}:{target.ClientId}";

                var command = await _dispatcher.CreateCommandAsync(
                    target.ClientId,
                    CommandType.UpgradeAgent,
                    payload: payload,
                    idempotencyKey: derivedKey,
                    createdBy: upgrade.CreatedBy,
                    ct: ct);

                target.Status = AgentUpgradeTargetStatus.Dispatched;
                target.CommandId = command.Id;
                target.DispatchedAt = now;
            }
            catch (BusinessException ex)
            {
                target.Status = AgentUpgradeTargetStatus.Failed;
                target.CompletedAt = now;
                target.ErrorCode = ex.ErrorCode;
                target.ErrorMessage = ex.Message;
            }
        }

        upgrade.CurrentBatch = batchIndex;
        upgrade.BatchStartedAt = now;
        if (upgrade.Status == AgentUpgradeStatus.Pending)
            upgrade.Status = AgentUpgradeStatus.Running;

        return targets;
    }

    private static AgentUpgradeSummaryDto ToSummary(AgentUpgrade upgrade, IEnumerable<AgentUpgradeTargetStatus> statuses)
    {
        var list = statuses.ToList();
        return new AgentUpgradeSummaryDto
        {
            Id = upgrade.Id,
            TargetVersion = upgrade.TargetVersion,
            PackageUrl = upgrade.PackageUrl,
            Note = upgrade.Note,
            Status = EnumMapping.ToSnakeCase(upgrade.Status),
            BatchPlan = upgrade.BatchPlan,
            CurrentBatch = upgrade.CurrentBatch,
            TotalCount = list.Count,
            SucceededCount = list.Count(s => s == AgentUpgradeTargetStatus.Succeeded),
            FailedCount = list.Count(s => s == AgentUpgradeTargetStatus.Failed),
            PendingCount = list.Count(s => s is AgentUpgradeTargetStatus.Waiting or AgentUpgradeTargetStatus.Dispatched),
            CreatedByName = upgrade.CreatedByName,
            CreatedAt = upgrade.CreatedAt,
            CompletedAt = upgrade.CompletedAt,
            FailureReason = upgrade.FailureReason
        };
    }

    /// <summary>
    /// 版本号比对。带后缀的信息版本号（1.2.0+abc123）在发布物上很常见，取第一段比；
    /// 解析不出来就退回字符串相等——**宁可判不成功，也不要判错成功**，
    /// 判错成功就是这次整改要消灭的那种假成功。
    /// </summary>
    internal static bool VersionMatches(string? reported, string target)
    {
        if (string.IsNullOrWhiteSpace(reported))
            return false;
        if (string.Equals(reported.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        static string Core(string value) => value.Split('+')[0].Trim();
        return Version.TryParse(Core(reported), out var a)
               && Version.TryParse(Core(target), out var b)
               && a == b;
    }

    /// <summary>分批计划归一化：空值取默认，异常值夹回，条数截断。</summary>
    internal static string NormalizeBatchPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return DefaultBatchPlan;

        var parts = plan.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var n) ? Math.Clamp(n, 0, 10000) : -1)
            .Where(n => n >= 0)
            .Take(10)
            .ToList();

        return parts.Count == 0 ? DefaultBatchPlan : string.Join(',', parts);
    }

    /// <summary>
    /// 把分批计划摊成实际的每批台数。
    ///
    /// 末位 0 表示「剩下的全放」；计划台数不够时最后补一批装下余数——
    /// 少算一台的后果是那台机器永远停在 waiting，而且不会有任何人发现。
    /// </summary>
    internal static List<int> ResolveBatchSizes(string plan, int total)
    {
        var sizes = new List<int>();
        var remaining = total;

        foreach (var part in plan.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (remaining <= 0)
                break;
            if (!int.TryParse(part, out var size))
                continue;

            var take = size <= 0 ? remaining : Math.Min(size, remaining);
            sizes.Add(take);
            remaining -= take;
        }

        if (remaining > 0)
            sizes.Add(remaining);

        return sizes;
    }

    /// <summary>
    /// 去掉信息版本号的构建后缀（1.1.0+346e5f98… → 1.1.0）。
    ///
    /// 踩过的坑：agent-version.txt 里写的是发布物的 ProductVersion，带 40 位提交号，
    /// 整串 45 个字符，而 targetVersion 这一列是 varchar(32)、DTO 上也是 MaxLength(32)。
    /// 自动填充直接把整串塞进表单，管理员点「下发」得到的是一句
    /// 「targetVersion 不能超过 32 字符」——**自动填充填出来的值自己过不了自己的校验**。
    /// 手工敲版本号的年代碰不到这一条，正因如此它只会在自动填充上线那天出现。
    ///
    /// 去掉后缀不损失任何判定能力：客户端上报的也是去掉后缀的版本号，
    /// 而「到底是哪一次构建」由升级包的 SHA-256 回答，比提交号更准。
    /// </summary>
    internal static string StripBuildSuffix(string version) => version.Split('+')[0].Trim();

    private static string ResolveContentPath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;
}
