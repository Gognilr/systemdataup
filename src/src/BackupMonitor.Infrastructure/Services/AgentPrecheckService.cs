using BackupMonitor.Shared.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>Agent 预检结果服务（设计书 13.1）</summary>
public interface IAgentPrecheckService
{
    Task<SubmitPrecheckResultResponse> SubmitResultAsync(
        Guid clientId, Guid taskId, SubmitPrecheckResultRequest request, CancellationToken ct = default);
}

/// <summary>预检结果实现</summary>
public class AgentPrecheckService : IAgentPrecheckService
{
    /// <summary>历史基线取样次数（R16）的 system_settings 键。</summary>
    public const string SizeBaselineSampleCountKey = "size_baseline_sample_count";

    /// <summary>总字节数低于中位数的这个比例即判可疑。</summary>
    public const string SizeBaselineMinBytesRatioKey = "size_baseline_min_bytes_ratio";

    /// <summary>文件数低于中位数的这个比例即判可疑（宽带宽，见 EvaluateSizeBaselineAsync）。</summary>
    public const string SizeBaselineMinFileRatioKey = "size_baseline_min_file_ratio";

    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAlertingService _alerting;
    private readonly IExecutionQueueService _queue;
    private readonly SystemSettingsProvider _settings;
    private readonly ILogger<AgentPrecheckService> _logger;

    public AgentPrecheckService(
        AppDbContext db,
        ICommandDispatcher dispatcher,
        IAlertingService alerting,
        IExecutionQueueService queue,
        SystemSettingsProvider settings,
        ILogger<AgentPrecheckService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _alerting = alerting;
        _queue = queue;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SubmitPrecheckResultResponse> SubmitResultAsync(
        Guid clientId, Guid taskId, SubmitPrecheckResultRequest request, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);
        if (task.ClientId != clientId)
            throw new BusinessException("FORBIDDEN", "任务不属于当前客户端", 403);

        var status = MapPrecheckStatus(request.PrecheckStatus);
        var failureCode = request.FailureCode;
        var failureMessage = request.FailureMessage;

        // Agent 通过 mTLS 证明了客户端身份，但预检字段仍然是客户端输入。
        // 服务端必须重新校验“通过”结果的完整性和任务级 requiredFiles，
        // 否则伪造一个 passed 就会触发自动上传。
        if (status == PrecheckStatus.Passed)
        {
            var validationError = ValidatePassedResult(task.RecognizerConfig, request);
            if (validationError is not null)
            {
                status = PrecheckStatus.RequiredFileMissing;
                failureCode = "SERVER_PRECHECK_VALIDATION_FAILED";
                failureMessage = validationError;
            }
        }

        // 幂等：同一个业务单元重复上报（指令重试、Agent 重启后补报）返回原结果。
        //
        // 去重键是 commandId + candidateKey，不能只认 commandId：
        // 一条预检指令说的是「这个任务扫一遍」，而一个任务下有多少个业务单元
        // （U8 一台机器 18 个账套是常态），Agent 就分多少次上报。只认 commandId 的那一版里，
        // 第 1 个账套写完就把闸关上，第 2..18 个全部被当成重复提交丢掉——
        // 不建候选、不下发上传、不告警，界面上一点痕迹都没有。
        if (request.CommandId is not null)
        {
            var command = await _db.Commands.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == request.CommandId, ct);
            if (command is not null && command.ClientId == clientId)
            {
                var already = PrecheckResultPayload.FindCandidate(command.ResultPayload, request.CandidateKey);
                if (already is not null)
                {
                    return new SubmitPrecheckResultResponse
                    {
                        CandidateBackupSetId = already,
                        Accepted = true,
                        NextAction = "duplicate_submission"
                    };
                }
            }
        }

        var now = DateTime.UtcNow;
        var isNew = false;

        // 候选备份集 upsert（按 task + candidate_key 去重）
        var candidate = await _db.CandidateBackupSets
            .FirstOrDefaultAsync(c => c.TaskId == taskId && c.CandidateKey == request.CandidateKey, ct);

        if (candidate is not null)
        {
            // 只有「还活着」的版本才算已入库。删掉的（回收站/已删除）行仍在表里，
            // 拿它们挡住重复预检，等于删掉一份备份之后源文件没变就再也备不回来。
            if (await _db.BackupSets.AnyAsync(
                    b => b.SourceCandidateId == candidate.Id && BackupSetStatuses.Live.Contains(b.Status), ct))
                throw new BusinessException("CANDIDATE_ALREADY_ARCHIVED", "该候选已正式入库，不可重复预检", 409);
        }
        else
        {
            candidate = new CandidateBackupSet
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                TaskId = taskId,
                CandidateKey = request.CandidateKey,
                DiscoveredAt = now,
                CreatedAt = now
            };
            _db.CandidateBackupSets.Add(candidate);
            isNew = true;
        }

        // 业务单元 upsert
        if (request.BusinessUnit is not null && !string.IsNullOrWhiteSpace(request.BusinessUnit.ExternalKey))
        {
            var unit = await _db.BusinessUnits
                .FirstOrDefaultAsync(u => u.TaskId == taskId && u.ExternalKey == request.BusinessUnit.ExternalKey, ct);
            if (unit is null)
            {
                unit = new BusinessUnit
                {
                    Id = Guid.NewGuid(),
                    TaskId = taskId,
                    ExternalKey = request.BusinessUnit.ExternalKey,
                    DisplayName = request.BusinessUnit.DisplayName ?? request.BusinessUnit.ExternalKey,
                    SourceRelativePath = request.BusinessUnit.SourceRelativePath,
                    LastDiscoveredAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _db.BusinessUnits.Add(unit);
            }
            else
            {
                unit.DisplayName = request.BusinessUnit.DisplayName ?? unit.DisplayName;
                unit.SourceRelativePath = request.BusinessUnit.SourceRelativePath ?? unit.SourceRelativePath;
                unit.LastDiscoveredAt = now;
                unit.UpdatedAt = now;
            }

            candidate.BusinessUnitId = unit.Id;
        }

        // 大小/文件数判定（R15 + R16）。
        //
        // 判定放服务端而不是 Agent：数据全都已经在预检回报里（TotalBytes / TotalFiles），
        // 放服务端不必改配置 DTO 和签名，旧版 Agent 也立刻受益。
        //
        // **判出可疑不再改 status**。原先它会把 status 顶成 SizeAbnormal，
        // 于是候选停在非 passed，UploadSessionService 抛 409、BatchOperationService 直接跳过——
        // 「今天的备份只有平时 10% 大小」时，系统的反应是一个字节都不收。
        // 现在照收入库、打标记、照发告警，判断权交给人。
        //
        // 放在业务单元 upsert 之后：历史基线必须按业务单元比，
        // 一个 U8 任务下 18 个账套大小差着数量级，按任务混在一起取中位数得到的是一个没有意义的数。
        string? sizeSuspicion = null;
        if (status == PrecheckStatus.Passed)
        {
            sizeSuspicion = ValidateSizeThresholds(task, request)
                ?? await EvaluateSizeBaselineAsync(task, candidate, request, ct);
        }

        candidate.SizeSuspicious = sizeSuspicion is not null;
        candidate.SizeSuspicionReason = sizeSuspicion;

        // 清单是否真的变了，必须在覆盖 ManifestHash 之前判断。
        var manifestUnchanged =
            !isNew
            && !string.IsNullOrWhiteSpace(candidate.ManifestHash)
            && string.Equals(candidate.ManifestHash, request.ManifestHash, StringComparison.OrdinalIgnoreCase)
            && candidate.TotalFiles == request.TotalFiles;

        candidate.SourceRoot = request.SourceRoot;
        candidate.BackupBusinessTime = request.BackupBusinessTime;
        candidate.PrecheckStatus = status;
        candidate.PrecheckedAt = now;
        candidate.TotalFiles = request.TotalFiles;
        candidate.TotalBytes = request.TotalBytes;
        candidate.ManifestHash = request.ManifestHash;

        // 快速指纹变了就推一次配置版本：Agent 的两段式扫描拿它当基线，
        // 而 Agent 只在配置版本变高时才重新拉配置。不推的话它会一直握着上上次的值，
        // 下一次「立即备份」必然对不上、退回整份重读——D2 想省掉的正是这一次读盘。
        var fingerprintChanged = !string.Equals(
            candidate.QuickFingerprint, request.QuickFingerprint, StringComparison.OrdinalIgnoreCase);
        candidate.QuickFingerprint = request.QuickFingerprint;

        // 源文件变了就清掉取消标记：管理员取消的是「刚才那一份」，不是「这个目录以后都别传」。
        // 内容没变时保留标记——那正是他刚刚说不要的那一份，重新扫一遍不构成新的意愿。
        if (!manifestUnchanged && candidate.CancelledAt is not null)
        {
            _logger.LogInformation(
                "候选 {Candidate} 的内容已变化，清除此前的取消标记", candidate.Id);
            candidate.CancelledAt = null;
            candidate.CancelledBy = null;
        }

        candidate.FailureCode = failureCode;
        candidate.FailureMessage = failureMessage;
        candidate.UpdatedAt = now;

        // 文件清单全量替换。
        //
        // manifestUnchanged 时整段跳过：同一份备份被重复扫到是常态（cron 每天扫、
        // 人再点一次「下发预检」），而重复扫到的清单逐字节相同——删掉几千行再原样插回去，
        // 除了搅动 WAL 什么都没做。更要紧的是 candidate_files 会被 upload_files 引用，
        // 每次重建都在给「引用已被删掉的行」制造机会。
        if (status == PrecheckStatus.Passed && request.Files is not null && !manifestUnchanged)
        {
            if (!isNew)
                await _db.Set<CandidateFile>().Where(f => f.CandidateBackupSetId == candidate.Id).ExecuteDeleteAsync(ct);

            var order = 0;
            foreach (var file in request.Files)
            {
                if (!PathSafety.IsValidRelativePath(file.RelativePath))
                    throw new BusinessException("INVALID_REQUEST", $"非法的文件相对路径：{file.RelativePath}", 400);

                _db.Set<CandidateFile>().Add(new CandidateFile
                {
                    Id = Guid.NewGuid(),
                    CandidateBackupSetId = candidate.Id,
                    RelativePath = file.RelativePath.Replace('\\', '/'),
                    FileName = Path.GetFileName(file.RelativePath.Replace('\\', '/')),
                    SizeBytes = file.SizeBytes,
                    LastModifiedAt = file.LastModifiedAt,
                    Sha256 = file.Sha256,
                    QuickHash = file.QuickHash,
                    IsRequired = file.IsRequired,
                    SortOrder = order++
                });
            }
        }

        // 任务最近预检状态
        task.LastPrecheckStatus = status;
        task.LastScanAt = now;

        if (fingerprintChanged && !string.IsNullOrWhiteSpace(request.QuickFingerprint))
            await BumpConfigRevisionAsync(clientId, ct);

        await _db.SaveChangesAsync(ct);

        // 失败告警
        if (sizeSuspicion is not null)
        {
            // 大小异常必须比其他预检失败更响：它意味着备份「跑了但是空的/残缺的」，
            // 归进 precheck_failed 的 Notice 等级等于埋掉。单独一个 category，
            // 重要级 high/critical 的任务再升一级到严重。
            //
            // 正文里明说「已经收下」：这条告警现在不再伴随拒收，
            // 不写清楚的话人会以为今天这一份没进来，跑去手工补一次。
            await _alerting.RaiseAsync(
                $"task:{task.Id}:precheck:size_abnormal",
                task.ImportanceLevel >= ImportanceLevel.High ? AlertLevel.Critical : AlertLevel.Warning,
                "size_abnormal",
                $"任务 {task.Name} 的备份大小可疑",
                sizeSuspicion + "。这一份已照常收下并标记为可疑，请人工确认它是否可用。",
                clientId: clientId,
                taskId: task.Id,
                businessUnitId: candidate.BusinessUnitId,
                ct: ct);
        }
        else if (status is not (PrecheckStatus.Passed or PrecheckStatus.NoNewBackup))
        {
            await _alerting.RaiseAsync(
                $"task:{task.Id}:precheck:{EnumMapping.ToSnakeCase(status)}",
                status == PrecheckStatus.Failed ? AlertLevel.Warning : AlertLevel.Notice,
                "precheck_failed",
                $"任务 {task.Name} 的备份没通过检查：{PlainText.Of(status)}",
                failureMessage,
                clientId: clientId,
                taskId: task.Id,
                businessUnitId: candidate.BusinessUnitId,
                ct: ct);
        }
        else
        {
            await _alerting.RecoverAsync($"task:{task.Id}:precheck:failed", ct);
            // 这一次的大小落回阈值内即视为恢复，否则大小异常告警会永久挂在告警中心。
            await _alerting.RecoverAsync($"task:{task.Id}:precheck:size_abnormal", ct);
        }

        // 下一步动作
        var accepted = status == PrecheckStatus.Passed;
        var nextAction = status switch
        {
            PrecheckStatus.Passed => task.TaskMode switch
            {
                TaskMode.Automatic => "wait_for_upload",
                TaskMode.ApprovalRequired => "wait_for_approval",
                _ => "wait_manual"
            },
            PrecheckStatus.NoNewBackup => "no_new_backup",
            _ => "rejected"
        };

        // 自动模式：预检通过立即下发上传指令。
        // 全局上传名额满了就改为排队（D3）——只堵手动点击那一条路的话，
        // 自动模式照样能把服务端暂存盘打满。名额腾出来时由 SequentialExecutionWorker 放行。
        //
        // 下发结果必须记下来。原先这里是「发了就当成了」，而 CreateCommandAsync 在幂等键
        // 命中一条还没跑完的旧指令时是原样返回、什么都不做的——于是界面照样说「已开始上传」，
        // 传输中却一直是空的，谁也说不出它卡在哪一步。
        Guid? uploadCommandId = null;
        string? uploadState = null;
        if (accepted && candidate.CancelledAt is not null)
        {
            // 管理员刚刚取消过这一份，而内容没有变化（变了的话上面已经把标记清掉了）。
            // 自动下发在这里必须停住：取消之后几分钟又自动重传一遍，
            // 使用者会认为取消这个按钮根本没有用。
            uploadState = PrecheckResultPayload.StateNotDispatched;
            _logger.LogInformation("候选 {Candidate} 已被管理员取消上传，本次不自动下发", candidate.Id);
        }
        else if (accepted && task.TaskMode == TaskMode.Automatic)
        {
            if (await _queue.TryDeferUploadAsync(clientId, task.Id, candidate.Id, ct))
            {
                uploadState = PrecheckResultPayload.StateQueued;
            }
            else
            {
                var uploadCommand = await _dispatcher.CreateCommandAsync(
                    clientId,
                    CommandType.UploadCandidate,
                    taskId: task.Id,
                    candidateBackupSetId: candidate.Id,
                    payload: new
                    {
                        candidateBackupSetId = candidate.Id,
                        bandwidthLimitKbps = task.BandwidthLimitKbps,
                        chunkSizeBytes = task.ChunkSizeBytes
                    },
                    priority: task.Priority,
                    idempotencyKey: $"auto-upload:{candidate.Id}",
                    ct: ct);

                uploadCommandId = uploadCommand.Id;
                uploadState = uploadCommand.Status switch
                {
                    CommandStatus.Pending => PrecheckResultPayload.StateDispatched,
                    CommandStatus.Claimed or CommandStatus.Running => PrecheckResultPayload.StateAlreadyRunning,
                    CommandStatus.Succeeded => PrecheckResultPayload.StateAlreadyDone,
                    _ => PrecheckResultPayload.StateNotDispatched
                };

                if (uploadState != PrecheckResultPayload.StateDispatched)
                {
                    _logger.LogWarning(
                        "候选 {Candidate} 的上传没有新下发：幂等键命中的指令 {Command} 处于 {Status}",
                        candidate.Id, uploadCommand.Id, uploadCommand.Status);
                }
            }
        }
        else if (accepted)
        {
            uploadState = PrecheckResultPayload.StateNotDispatched;
        }

        // 指令结果回填。
        //
        // 这里只往清单里加一项，不再改指令状态——一个任务有多少个业务单元就有多少次上报，
        // 在第 1 个单元就把指令判成终态的那一版里：后面单元的进度上报全部 409 被吞
        // （界面进度就此定格），界面把第 1 个单元的结论当成整个任务的结论，
        // 第 1 个单元恰好是 no_new_backup 时，另外 17 个有新备份的账套一个都不会传。
        // 指令的终结交给 Agent 扫完之后的 ReportCompleted，由它汇总全部单元。
        if (request.CommandId is not null)
        {
            var command = await _db.Commands.FirstOrDefaultAsync(c => c.Id == request.CommandId, ct);
            if (command is not null && command.ClientId == clientId && command.TaskId == taskId)
            {
                command.ResultPayload = PrecheckResultPayload.Upsert(command.ResultPayload, new PrecheckResultPayload.Entry(
                    request.CandidateKey,
                    candidate.Id,
                    EnumMapping.ToSnakeCase(status),
                    uploadCommandId,
                    uploadState,
                    request.BusinessUnit?.DisplayName));
                await _db.SaveChangesAsync(ct);
            }
        }

        _logger.LogInformation(
            "预检结果已提交 task={TaskId} candidate={CandidateKey} status={Status} upload={UploadState}",
            task.Id, candidate.CandidateKey, status, uploadState ?? "-");

        return new SubmitPrecheckResultResponse
        {
            CandidateBackupSetId = candidate.Id,
            Accepted = accepted,
            NextAction = nextAction
        };
    }

    private static PrecheckStatus MapPrecheckStatus(string value) =>
        EnumMapping.ParseSnakeCase<PrecheckStatus>(value, "precheckStatus");

    /// <summary>
    /// 任务级大小/文件数**固定**阈值判定（整改 A3）。三个阈值都留空（null 或 &lt;= 0）时返回 null。
    /// 仅在预检自身已判定为「通过」后调用——失败结果的 TotalBytes/TotalFiles 本来就不可信。
    ///
    /// 这一层保留为硬下限：它抓的是「绝对不该低于这个数」，由人显式配出来。
    /// 抓不到的那个更有价值的信号（「今天这份比过去 N 次的中位数小了 90%」）
    /// 由 <see cref="EvaluateSizeBaselineAsync"/> 负责。
    ///
    /// 返回值是「可疑理由」，不再是「拒收理由」——判出来也照收入库。
    /// </summary>
    private static string? ValidateSizeThresholds(BackupTask task, SubmitPrecheckResultRequest request)
    {
        var bytes = request.TotalBytes ?? 0;
        var files = request.TotalFiles ?? 0;

        if (task.MinTotalBytes is > 0 && bytes < task.MinTotalBytes)
            return $"备份总大小 {bytes} 字节低于下限 {task.MinTotalBytes} 字节——备份可能未正常生成";
        if (task.MaxTotalBytes is > 0 && bytes > task.MaxTotalBytes)
            return $"备份总大小 {bytes} 字节超过上限 {task.MaxTotalBytes} 字节";
        if (task.MinFileCount is > 0 && files < task.MinFileCount)
            return $"备份文件数 {files} 少于下限 {task.MinFileCount}";

        return null;
    }

    /// <summary>
    /// 历史基线偏离判定（R16）。
    ///
    /// 固定阈值是死数，绝大多数任务上根本没人配；而最有价值的信号是相对的——
    /// 「今天这份比过去 N 次的中位数小了 90%」。结构快检（原 R14）已决定不做，
    /// 「备份跑了但是空的 / 只剩十分之一」这类信号现在全靠这一条兜底。
    ///
    /// 三条刻意的设计：
    /// 1. 用**中位数**不用平均值：一次异常的小备份会把平均值拉下去，
    ///    于是下一次同样小的备份反而落在「正常」范围里——判据被它要抓的东西污染了。
    /// 2. 样本不足 N 次就不判。新任务不该因为没有历史就报警
    ///    （与漏备份巡检「刚建出来的任务不算漏」同一条原则）。
    /// 3. 按**业务单元**取样。一个 U8 任务下 18 个账套大小差着数量级，
    ///    混在一起取中位数得到的是一个没有意义的数。
    ///
    /// 文件数用宽带宽（默认掉到五分之一才说话）：U8 附件库个数随启用年度不同，
    /// 个数波动是常态，不能拿它当缺失判据。真正敏感的是总字节数。
    /// </summary>
    private async Task<string?> EvaluateSizeBaselineAsync(
        BackupTask task,
        CandidateBackupSet candidate,
        SubmitPrecheckResultRequest request,
        CancellationToken ct)
    {
        var sampleCount = Math.Clamp(
            await _settings.GetIntAsync(SizeBaselineSampleCountKey, 10, ct), 3, 200);
        var bytesRatio = Math.Clamp(
            await _settings.GetDoubleAsync(SizeBaselineMinBytesRatioKey, 0.5, ct), 0.01, 1.0);
        var fileRatio = Math.Clamp(
            await _settings.GetDoubleAsync(SizeBaselineMinFileRatioKey, 0.2, ct), 0.01, 1.0);

        var samples = await _db.BackupSets.AsNoTracking()
            .Where(b => b.TaskId == task.Id
                        && b.BusinessUnitId == candidate.BusinessUnitId
                        && BackupSetStatuses.Live.Contains(b.Status))
            .OrderByDescending(b => b.UploadedAt)
            .Select(b => new { b.TotalBytes, b.TotalFiles })
            .Take(sampleCount)
            .ToListAsync(ct);

        if (samples.Count < sampleCount)
            return null;

        var medianBytes = Median(samples.Select(s => (double)s.TotalBytes));
        var medianFiles = Median(samples.Select(s => (double)s.TotalFiles));

        var bytes = request.TotalBytes ?? 0;
        var files = request.TotalFiles ?? 0;

        if (medianBytes > 0 && bytes < medianBytes * bytesRatio)
        {
            return $"备份总大小 {FormatBytes(bytes)} 只有最近 {sampleCount} 次中位数 "
                 + $"{FormatBytes((long)medianBytes)} 的 {bytes * 100.0 / medianBytes:0.#}%"
                 + $"（低于 {bytesRatio * 100:0.#}% 即判可疑）——备份可能跑了但是空的或只剩一部分";
        }

        if (medianFiles > 0 && files < medianFiles * fileRatio)
        {
            return $"备份文件数 {files} 只有最近 {sampleCount} 次中位数 {medianFiles:0.#} 的 "
                 + $"{files * 100.0 / medianFiles:0.#}%（低于 {fileRatio * 100:0.#}% 即判可疑）";
        }

        return null;
    }

    /// <summary>中位数。偶数个样本取中间两个的平均。</summary>
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            return 0;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>告警正文里的字节数要给人读，不是给机器读——原始字节数看不出量级。</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static string? ValidatePassedResult(string recognizerConfig, SubmitPrecheckResultRequest request)
    {
        var files = request.Files;
        if (files is null || files.Count == 0)
            return "预检通过结果必须包含文件清单";
        if (request.TotalFiles is null || request.TotalFiles.Value != files.Count)
            return "TotalFiles 与文件清单数量不一致";
        if (request.TotalBytes is null || request.TotalBytes.Value != files.Sum(f => f.SizeBytes))
            return "TotalBytes 与文件清单大小不一致";
        if (!IsSha256(request.ManifestHash))
            return "预检通过结果必须包含 64 位 ManifestHash";
        if (files.Any(file => file.SizeBytes < 0
                              || !PathSafety.IsValidRelativePath(file.RelativePath)
                              || !IsSha256(file.Sha256)))
            return "文件清单包含非法路径、大小或 SHA-256";
        if (files.GroupBy(file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            return "文件清单包含重复相对路径";

        var manifest = string.Join('\n', files
            .Select(file => new
            {
                RelativePath = file.RelativePath.Replace('\\', '/'),
                file.SizeBytes,
                file.LastModifiedAt,
                Sha256 = file.Sha256!.ToLowerInvariant()
            })
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(file => $"{file.RelativePath}|{file.SizeBytes}|{file.LastModifiedAt.Ticks}|{file.Sha256}"));
        var actualManifest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        if (!string.Equals(actualManifest, request.ManifestHash, StringComparison.OrdinalIgnoreCase))
            return "ManifestHash 与文件清单不一致";

        // requiredFiles 的读取与匹配语义共用 RecognizerRules：这套判定原先在
        // Agent 扫描、服务端校验、配置向导三处各写了一份，任何一处改了都不会有人发现
        // 另外两处没改——而它们判的是同一件事「这份备份完整吗」。
        var required = RecognizerRules.Parse(recognizerConfig).Required
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var relativePaths = files.Select(file => file.RelativePath.Replace('\\', '/')).ToList();
        var missing = required.FirstOrDefault(pattern =>
            !relativePaths.Any(path => RecognizerRules.MatchesPath(path, pattern)));
        return missing is null ? null : $"缺少 requiredFiles 文件：{missing}";
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(value, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 推高客户端配置修订号，让下一次心跳把配置重新拉一遍。
    /// 口径与 MonitoredServiceAdminService 一致：取「任务版本最大值与当前修订号的较大者」再加一，
    /// 保证 ComputeVersionAsync 的 max(任务版本, 修订号) 确实变大。
    /// </summary>
    private async Task BumpConfigRevisionAsync(Guid clientId, CancellationToken ct)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return;

        var taskVersion = await _db.BackupTasks
            .Where(t => t.ClientId == clientId)
            .Select(t => (long?)t.ConfigVersion)
            .MaxAsync(ct) ?? 0;

        client.ConfigRevision = Math.Max(client.ConfigRevision, taskVersion) + 1;
    }
}
