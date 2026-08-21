using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
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
    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAlertingService _alerting;
    private readonly ILogger<AgentPrecheckService> _logger;

    public AgentPrecheckService(
        AppDbContext db,
        ICommandDispatcher dispatcher,
        IAlertingService alerting,
        ILogger<AgentPrecheckService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _alerting = alerting;
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

        // 幂等：同一预检指令重复上报返回原结果
        if (request.CommandId is not null)
        {
            var command = await _db.Commands.FirstOrDefaultAsync(c => c.Id == request.CommandId, ct);
            if (command is not null && command.ClientId == clientId
                && !string.IsNullOrWhiteSpace(command.ResultPayload))
            {
                try
                {
                    using var doc = JsonDocument.Parse(command.ResultPayload);
                    if (doc.RootElement.TryGetProperty("candidateBackupSetId", out var el)
                        && el.ValueKind == JsonValueKind.String
                        && Guid.TryParse(el.GetString(), out var existingId))
                    {
                        return new SubmitPrecheckResultResponse
                        {
                            CandidateBackupSetId = existingId,
                            Accepted = true,
                            NextAction = "duplicate_submission"
                        };
                    }
                }
                catch (JsonException)
                {
                    // 非结构化结果，按首次上报处理
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
            if (await _db.BackupSets.AnyAsync(b => b.SourceCandidateId == candidate.Id, ct))
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

        candidate.SourceRoot = request.SourceRoot;
        candidate.BackupBusinessTime = request.BackupBusinessTime;
        candidate.PrecheckStatus = status;
        candidate.PrecheckedAt = now;
        candidate.TotalFiles = request.TotalFiles;
        candidate.TotalBytes = request.TotalBytes;
        candidate.ManifestHash = request.ManifestHash;
        candidate.QuickFingerprint = request.QuickFingerprint;
        candidate.FailureCode = failureCode;
        candidate.FailureMessage = failureMessage;
        candidate.UpdatedAt = now;

        // 文件清单全量替换
        if (status == PrecheckStatus.Passed && request.Files is not null)
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

        await _db.SaveChangesAsync(ct);

        // 指令结果回填（幂等依据）
        if (request.CommandId is not null)
        {
            var command = await _db.Commands.FirstOrDefaultAsync(c => c.Id == request.CommandId, ct);
            if (command is not null && command.ClientId == clientId && command.TaskId == taskId)
            {
                command.ResultPayload = JsonSerializer.Serialize(new { candidateBackupSetId = candidate.Id });
                if (command.Status is CommandStatus.Claimed or CommandStatus.Running)
                {
                    command.Status = status == PrecheckStatus.Passed ? CommandStatus.Succeeded : CommandStatus.Failed;
                    command.CompletedAt = now;
                    command.ResultCode = EnumMapping.ToSnakeCase(status);
                }
                await _db.SaveChangesAsync(ct);
            }
        }

        // 失败告警
        if (status is not (PrecheckStatus.Passed or PrecheckStatus.NoNewBackup))
        {
            await _alerting.RaiseAsync(
                $"task:{task.Id}:precheck:{EnumMapping.ToSnakeCase(status)}",
                status == PrecheckStatus.Failed ? AlertLevel.Warning : AlertLevel.Notice,
                "precheck_failed",
                $"任务 {task.Name} 预检未通过：{EnumMapping.ToSnakeCase(status)}",
                failureMessage,
                clientId: clientId,
                taskId: task.Id,
                businessUnitId: candidate.BusinessUnitId,
                ct: ct);
        }
        else
        {
            await _alerting.RecoverAsync($"task:{task.Id}:precheck:failed", ct);
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

        // 自动模式：预检通过立即下发上传指令
        if (accepted && task.TaskMode == TaskMode.Automatic)
        {
            await _dispatcher.CreateCommandAsync(
                clientId,
                CommandType.UploadCandidate,
                taskId: task.Id,
                candidateBackupSetId: candidate.Id,
                payload: new
                {
                    candidateBackupSetId = candidate.Id,
                    bandwidthLimitKbps = task.BandwidthLimitKbps
                },
                priority: task.Priority,
                idempotencyKey: $"auto-upload:{candidate.Id}",
                ct: ct);
        }

        _logger.LogInformation(
            "预检结果已提交 task={TaskId} candidate={CandidateKey} status={Status}",
            task.Id, candidate.CandidateKey, status);

        return new SubmitPrecheckResultResponse
        {
            CandidateBackupSetId = candidate.Id,
            Accepted = accepted,
            NextAction = nextAction
        };
    }

    private static PrecheckStatus MapPrecheckStatus(string value) =>
        EnumMapping.ParseSnakeCase<PrecheckStatus>(value, "precheckStatus");

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

        var required = ReadRequiredPatterns(recognizerConfig);
        var relativePaths = files.Select(file => file.RelativePath.Replace('\\', '/')).ToList();
        var missing = required.FirstOrDefault(pattern =>
            !relativePaths.Any(path => GlobMatch(path, pattern) || GlobMatch(Path.GetFileName(path), pattern)));
        return missing is null ? null : $"缺少 requiredFiles 文件：{missing}";
    }

    private static List<string> ReadRequiredPatterns(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new[] { "requiredFiles", "requiredPatterns", "required" }
                .SelectMany(property =>
                    root.TryGetProperty(property, out var value)
                        ? value.ValueKind == JsonValueKind.String
                            ? string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!]
                            : value.ValueKind == JsonValueKind.Array
                                ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                                    .Select(item => item.GetString()!)
                                    .Where(item => !string.IsNullOrWhiteSpace(item))
                                : []
                        : [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(value, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    private static bool GlobMatch(string value, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value.Replace('\\', '/'), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
