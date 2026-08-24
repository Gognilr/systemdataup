using System.Text.Json;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>管理端备份任务服务（设计书 16 任务管理接口）</summary>
public interface IBackupTaskService
{
    Task<BackupTaskDetailDto> CreateAsync(CreateBackupTaskRequest request, CancellationToken ct = default);
    Task<BackupTaskDetailDto> UpdateAsync(Guid taskId, UpdateBackupTaskRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid taskId, CancellationToken ct = default);
    Task<PagedResult<BackupTaskListItemDto>> GetListAsync(BackupTaskQuery query, CancellationToken ct = default);
    Task<BackupTaskDetailDto> GetDetailAsync(Guid taskId, CancellationToken ct = default);
    Task<BackupTaskDetailDto> PauseAsync(Guid taskId, CancellationToken ct = default);
    Task<BackupTaskDetailDto> ResumeAsync(Guid taskId, CancellationToken ct = default);
    Task<DispatchCommandResponse> DispatchPrecheckAsync(Guid taskId, CancellationToken ct = default);
    Task<DispatchCommandResponse> DispatchUploadAsync(Guid taskId, DispatchUploadRequest request, CancellationToken ct = default);
    Task<TestRecognitionResponse> TestRecognitionAsync(Guid taskId, CancellationToken ct = default);
}

/// <summary>备份任务服务实现（CRUD 乐观锁 + 暂停/恢复 + 指令下发 + 识别测试）</summary>
public class BackupTaskService : IBackupTaskService
{

    // ── 列表排序白名单（审查 P1-3）
    private static readonly Dictionary<string, Func<IQueryable<BackupTask>, bool, IQueryable<BackupTask>>> TaskSorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = SortWhitelist.By<BackupTask, string>(t => t.Name),
            ["applicationName"] = SortWhitelist.By<BackupTask, string?>(t => t.ApplicationName),
            ["taskMode"] = SortWhitelist.By<BackupTask, TaskMode>(t => t.TaskMode),
            ["enabled"] = SortWhitelist.By<BackupTask, bool>(t => t.Enabled),
            ["lastSuccessAt"] = SortWhitelist.By<BackupTask, DateTime?>(t => t.LastSuccessAt),
            ["lastScanAt"] = SortWhitelist.By<BackupTask, DateTime?>(t => t.LastScanAt),
            ["createdAt"] = SortWhitelist.By<BackupTask, DateTime>(t => t.CreatedAt)
        };

    private static readonly Func<IQueryable<BackupTask>, bool, IQueryable<BackupTask>> TaskSortFallback =
        SortWhitelist.By<BackupTask, DateTime>(t => t.CreatedAt);
    private const int MinChunkSize = 4 * 1024 * 1024;   // 4MB（设计书 7.2）
    private const int MaxChunkSize = 32 * 1024 * 1024;  // 32MB

    private static readonly AlertStatus[] ActiveAlertStatuses =
        [AlertStatus.Open, AlertStatus.Acknowledged, AlertStatus.InProgress];

    private static readonly UploadStatus[] ActiveUploadStatuses =
    [
        UploadStatus.Created, UploadStatus.WaitingPermission, UploadStatus.Uploading,
        UploadStatus.Paused, UploadStatus.RetryWait, UploadStatus.Received, UploadStatus.Verifying
    ];

    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _commands;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<BackupTaskService> _logger;

    public BackupTaskService(
        AppDbContext db,
        ICommandDispatcher commands,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<BackupTaskService> logger)
    {
        _db = db;
        _commands = commands;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task<BackupTaskDetailDto> CreateAsync(CreateBackupTaskRequest request, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ClientId, ct)
            ?? throw new NotFoundException("客户端", request.ClientId);

        if (client.Status == ClientStatus.PendingApproval)
            throw new BusinessException("CONFLICT", "客户端尚未审批通过，不能创建备份任务", 409);
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CONFLICT", "客户端已禁用或已注销，不能创建备份任务", 409);

        var duplicate = await _db.BackupTasks.AnyAsync(t =>
            t.ClientId == request.ClientId && t.Name == request.Name, ct);
        if (duplicate)
            throw new BusinessException("DUPLICATE_TASK_NAME", $"该客户端已存在同名任务：{request.Name}", 409);

        var task = new BackupTask { Id = Guid.NewGuid(), ClientId = request.ClientId };
        ApplyEditableFields(task, request.Name, request.ApplicationName, request.SourcePath,
            request.RecognizerType, request.TaskMode, request.Enabled, request.Priority,
            request.ImportanceLevel, request.ScanSchedule, request.UploadWindowStart, request.UploadWindowEnd,
            request.ScheduleTimezone, request.RandomDelayMinutes, request.StabilityIntervalSeconds,
            request.MaxStabilityWaitSeconds, request.MinTotalBytes, request.MaxTotalBytes,
            request.MinFileCount, request.BandwidthLimitKbps, request.ChunkSizeBytes,
            request.RetryCount, request.RetryIntervalSeconds, request.RetentionPolicyId,
            request.RecognizerConfig, request.AlertConfig);

        task.ConfigVersion = 1;

        _db.BackupTasks.Add(task);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("task.create", AuditResult.Success, "backup_task", task.Id,
            afterData: JsonSerializer.Serialize(new { task.Id, task.ClientId, task.Name }), ct: ct);

        _logger.LogInformation("备份任务已创建 taskId={TaskId} name={Name} clientId={ClientId}",
            task.Id, task.Name, task.ClientId);

        return await GetDetailAsync(task.Id, ct);
    }

    public async Task<BackupTaskDetailDto> UpdateAsync(Guid taskId, UpdateBackupTaskRequest request, CancellationToken ct = default)
    {
        if (request.RowVersion is null)
            throw new ValidationFailedException("修改任务必须携带 rowVersion（乐观锁版本号）");

        var task = await _db.BackupTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        if (task.RowVersion != request.RowVersion.Value)
            throw new ConcurrencyConflictException("备份任务");

        var before = JsonSerializer.Serialize(new
        {
            task.Name, task.ApplicationName, task.SourcePath,
            RecognizerType = EnumMapping.ToSnakeCase(task.RecognizerType),
            TaskMode = EnumMapping.ToSnakeCase(task.TaskMode),
            task.Enabled, task.ConfigVersion
        });

        ApplyEditableFields(task, request.Name, request.ApplicationName, request.SourcePath,
            request.RecognizerType, request.TaskMode, request.Enabled, request.Priority,
            request.ImportanceLevel, request.ScanSchedule, request.UploadWindowStart, request.UploadWindowEnd,
            request.ScheduleTimezone, request.RandomDelayMinutes, request.StabilityIntervalSeconds,
            request.MaxStabilityWaitSeconds, request.MinTotalBytes, request.MaxTotalBytes,
            request.MinFileCount, request.BandwidthLimitKbps, request.ChunkSizeBytes,
            request.RetryCount, request.RetryIntervalSeconds, request.RetentionPolicyId,
            request.RecognizerConfig, request.AlertConfig);

        // 任何配置变更都递增版本号，客户端心跳将感知并重新拉取配置（设计书 11.2）
        task.ConfigVersion++;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("备份任务");
        }

        await _audit.RecordAsync("task.update", AuditResult.Success, "backup_task", task.Id,
            beforeData: before,
            afterData: JsonSerializer.Serialize(new { task.Name, task.ConfigVersion }), ct: ct);

        return await GetDetailAsync(task.Id, ct);
    }

    public async Task DeleteAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        var hasSets = await _db.BackupSets.AnyAsync(s => s.TaskId == taskId, ct);
        if (hasSets)
            throw new BusinessException("CONFLICT", "任务已有入库备份版本，不能删除（请先处理保留策略）", 409);

        var hasCandidates = await _db.CandidateBackupSets.AnyAsync(c => c.TaskId == taskId, ct);
        if (hasCandidates)
            throw new BusinessException("CONFLICT", "任务存在候选备份集，不能删除", 409);

        var hasActiveSessions = await _db.UploadSessions.AnyAsync(s =>
            s.TaskId == taskId && ActiveUploadStatuses.Contains(s.Status), ct);
        if (hasActiveSessions)
            throw new BusinessException("CONFLICT", "任务存在活动上传会话，不能删除", 409);

        // 未完成的指令一并取消
        await _db.Commands
            .Where(c => c.TaskId == taskId && c.Status == CommandStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CommandStatus.Cancelled)
                .SetProperty(c => c.CompletedAt, DateTime.UtcNow), ct);

        _db.BackupTasks.Remove(task);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("task.delete", AuditResult.Success, "backup_task", taskId,
            beforeData: JsonSerializer.Serialize(new { task.Id, task.Name, task.ClientId }), ct: ct);
    }

    public async Task<PagedResult<BackupTaskListItemDto>> GetListAsync(BackupTaskQuery query, CancellationToken ct = default)
    {
        var tasks = _db.BackupTasks.AsNoTracking().AsQueryable();

        if (query.ClientId is not null)
            tasks = tasks.Where(t => t.ClientId == query.ClientId);

        if (query.GroupId is not null)
            tasks = tasks.Where(t => t.Client.ClientGroupId == query.GroupId);

        if (!string.IsNullOrWhiteSpace(query.TaskMode))
        {
            if (!EnumMapping.TryParseSnakeCase<TaskMode>(query.TaskMode, out var mode))
                throw new BusinessException("INVALID_REQUEST", $"无效的任务模式：{query.TaskMode}", 400);
            tasks = tasks.Where(t => t.TaskMode == mode);
        }

        if (query.Enabled is not null)
            tasks = tasks.Where(t => t.Enabled == query.Enabled);

        if (!string.IsNullOrWhiteSpace(query.ApplicationName))
            tasks = tasks.Where(t => EF.Functions.ILike(t.ApplicationName, $"%{query.ApplicationName.Trim()}%"));

        if (query.LastSuccessBefore is not null)
            tasks = tasks.Where(t => t.LastSuccessAt == null || t.LastSuccessAt < query.LastSuccessBefore);

        if (query.HasAlert is true)
        {
            tasks = tasks.Where(t => _db.Alerts.Any(a =>
                a.TaskId == t.Id && ActiveAlertStatuses.Contains(a.Status)));
        }
        else if (query.HasAlert is false)
        {
            tasks = tasks.Where(t => !_db.Alerts.Any(a =>
                a.TaskId == t.Id && ActiveAlertStatuses.Contains(a.Status)));
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            tasks = tasks.Where(t =>
                EF.Functions.ILike(t.Name, $"%{keyword}%")
                || EF.Functions.ILike(t.ApplicationName, $"%{keyword}%"));
        }

        var totalCount = await tasks.LongCountAsync(ct);

        var rows = await tasks
            .ApplySort(query.SortBy, query.SortDescending, TaskSorts, TaskSortFallback)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(t => new
            {
                t.Id,
                t.ClientId,
                ClientHostname = t.Client.Hostname,
                t.Name,
                t.ApplicationName,
                t.SourcePath,
                t.RecognizerType,
                t.TaskMode,
                t.Enabled,
                t.ImportanceLevel,
                t.LastPrecheckStatus,
                t.LastScanAt,
                t.LastSuccessAt,
                t.ConfigVersion
            })
            .ToListAsync(ct);

        var pageIds = rows.Select(r => r.Id).ToList();
        var alertCounts = await _db.Alerts
            .Where(a => a.TaskId != null && pageIds.Contains(a.TaskId.Value)
                        && ActiveAlertStatuses.Contains(a.Status))
            .GroupBy(a => a.TaskId!.Value)
            .Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var items = rows.Select(r => new BackupTaskListItemDto
        {
            Id = r.Id,
            ClientId = r.ClientId,
            ClientHostname = r.ClientHostname,
            Name = r.Name,
            ApplicationName = r.ApplicationName,
            SourcePath = r.SourcePath,
            RecognizerType = EnumMapping.ToSnakeCase(r.RecognizerType),
            TaskMode = EnumMapping.ToSnakeCase(r.TaskMode),
            Enabled = r.Enabled,
            ImportanceLevel = EnumMapping.ToSnakeCase(r.ImportanceLevel),
            LastPrecheckStatus = r.LastPrecheckStatus.HasValue
                ? EnumMapping.ToSnakeCase(r.LastPrecheckStatus.Value) : null,
            LastScanAt = r.LastScanAt,
            LastSuccessAt = r.LastSuccessAt,
            ConfigVersion = r.ConfigVersion,
            ActiveAlertCount = alertCounts.FirstOrDefault(a => a.TaskId == r.Id)?.Count ?? 0
        }).ToList();

        return PagedResult<BackupTaskListItemDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<BackupTaskDetailDto> GetDetailAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.AsNoTracking()
            .Include(t => t.Client)
            .Include(t => t.RetentionPolicy)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        var activeAlertCount = await _db.Alerts.CountAsync(a =>
            a.TaskId == taskId && ActiveAlertStatuses.Contains(a.Status), ct);

        return new BackupTaskDetailDto
        {
            Id = task.Id,
            ClientId = task.ClientId,
            ClientHostname = task.Client.Hostname,
            Name = task.Name,
            ApplicationName = task.ApplicationName,
            SourcePath = task.SourcePath,
            RecognizerType = EnumMapping.ToSnakeCase(task.RecognizerType),
            TaskMode = EnumMapping.ToSnakeCase(task.TaskMode),
            Enabled = task.Enabled,
            ImportanceLevel = EnumMapping.ToSnakeCase(task.ImportanceLevel),
            LastPrecheckStatus = task.LastPrecheckStatus.HasValue
                ? EnumMapping.ToSnakeCase(task.LastPrecheckStatus.Value) : null,
            LastScanAt = task.LastScanAt,
            LastSuccessAt = task.LastSuccessAt,
            ConfigVersion = task.ConfigVersion,
            ActiveAlertCount = activeAlertCount,

            TemplateId = task.TemplateId,
            Priority = task.Priority,
            ScanSchedule = task.ScanSchedule,
            UploadWindowStart = task.UploadWindowStart?.ToString(@"hh\:mm"),
            UploadWindowEnd = task.UploadWindowEnd?.ToString(@"hh\:mm"),
            ScheduleTimezone = task.ScheduleTimezone,
            RandomDelayMinutes = task.RandomDelayMinutes,
            StabilityIntervalSeconds = task.StabilityIntervalSeconds,
            MaxStabilityWaitSeconds = task.MaxStabilityWaitSeconds,
            MinTotalBytes = task.MinTotalBytes,
            MaxTotalBytes = task.MaxTotalBytes,
            MinFileCount = task.MinFileCount,
            BandwidthLimitKbps = task.BandwidthLimitKbps,
            ChunkSizeBytes = task.ChunkSizeBytes,
            RetryCount = task.RetryCount,
            RetryIntervalSeconds = task.RetryIntervalSeconds,
            RetentionPolicyId = task.RetentionPolicyId,
            RetentionPolicyName = task.RetentionPolicy?.Name,
            RecognizerConfig = task.RecognizerConfig,
            AlertConfig = task.AlertConfig,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            RowVersion = task.RowVersion
        };
    }

    /// <summary>暂停任务（设计书 16.5，记录暂停前模式以便恢复）</summary>
    public async Task<BackupTaskDetailDto> PauseAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        if (task.TaskMode == TaskMode.Paused)
            throw new BusinessException("CONFLICT", "任务已处于暂停状态", 409);

        task.PreviousTaskMode = task.TaskMode;
        task.TaskMode = TaskMode.Paused;
        task.ConfigVersion++;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("task.pause", AuditResult.Success, "backup_task", task.Id, ct: ct);

        return await GetDetailAsync(task.Id, ct);
    }

    /// <summary>恢复任务（设计书 16.6，还原暂停前模式）</summary>
    public async Task<BackupTaskDetailDto> ResumeAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        if (task.TaskMode != TaskMode.Paused)
            throw new BusinessException("CONFLICT", "任务未处于暂停状态", 409);

        task.TaskMode = task.PreviousTaskMode ?? TaskMode.ApprovalRequired;
        task.PreviousTaskMode = null;
        task.ConfigVersion++;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("task.resume", AuditResult.Success, "backup_task", task.Id, ct: ct);

        return await GetDetailAsync(task.Id, ct);
    }

    /// <summary>手动下发预检（设计书 16.6）</summary>
    public async Task<DispatchCommandResponse> DispatchPrecheckAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.AsNoTracking()
            .Include(t => t.Client)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        EnsureClientDispatchable(task.Client);

        var command = await _commands.CreateCommandAsync(
            task.ClientId, CommandType.PrecheckTask,
            taskId: task.Id,
            priority: task.Priority,
            createdBy: _context.UserId,
            idempotencyKey: $"manual-precheck:{task.Id}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            ct: ct);

        return new DispatchCommandResponse { CommandId = command.Id };
    }

    /// <summary>审批/手动下发上传（设计书 16.7）</summary>
    public async Task<DispatchCommandResponse> DispatchUploadAsync(Guid taskId, DispatchUploadRequest request, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.AsNoTracking()
            .Include(t => t.Client)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        EnsureClientDispatchable(task.Client);

        var candidate = await _db.CandidateBackupSets.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.CandidateBackupSetId, ct)
            ?? throw new NotFoundException("候选备份集", request.CandidateBackupSetId);

        if (candidate.TaskId != taskId)
            throw new BusinessException("FORBIDDEN", "候选备份集不属于该任务", 403);

        if (candidate.PrecheckStatus != PrecheckStatus.Passed)
            throw new BusinessException("CONFLICT",
                $"候选预检状态为 {EnumMapping.ToSnakeCase(candidate.PrecheckStatus)}，只有预检通过的候选可以上传", 409);

        if (candidate.ExpiresAt is not null && candidate.ExpiresAt < DateTime.UtcNow)
            throw new BusinessException("CANDIDATE_EXPIRED", "候选备份集已过期，请重新预检", 409);

        if (candidate.SupersededById is not null)
            throw new BusinessException("CANDIDATE_SUPERSEDED", "候选备份集已被新候选替代", 409);

        var archived = await _db.BackupSets.AnyAsync(s => s.SourceCandidateId == candidate.Id, ct);
        if (archived)
            throw new BusinessException("CANDIDATE_ALREADY_ARCHIVED", "候选备份集已入库", 409);

        if (!request.Force)
        {
            var busy = await _db.UploadSessions.AnyAsync(s =>
                s.ClientId == task.ClientId && ActiveUploadStatuses.Contains(s.Status), ct);
            if (busy)
                throw new BusinessException("CLIENT_BUSY", "客户端存在活动上传，可稍后重试或使用强制下发", 409);

            if (!IsWithinUploadWindow(task))
                throw new BusinessException("OUT_OF_UPLOAD_WINDOW", "当前不在任务配置的上传窗口内，可使用强制下发忽略", 409);
        }

        var command = await _commands.CreateCommandAsync(
            task.ClientId, CommandType.UploadCandidate,
            taskId: task.Id,
            candidateBackupSetId: candidate.Id,
            payload: new
            {
                candidateBackupSetId = candidate.Id,
                bandwidthLimitKbps = request.BandwidthLimitKbps ?? task.BandwidthLimitKbps,
                chunkSizeBytes = task.ChunkSizeBytes,
                force = request.Force
            },
            priority: task.Priority,
            idempotencyKey: $"manual-upload:{candidate.Id}",
            createdBy: _context.UserId,
            ct: ct);

        await _audit.RecordAsync("task.dispatch_upload", AuditResult.Success, "backup_task", task.Id,
            afterData: JsonSerializer.Serialize(new { CandidateId = candidate.Id, CommandId = command.Id, Force = request.Force }), ct: ct);

        return new DispatchCommandResponse { CommandId = command.Id };
    }

    /// <summary>识别测试（设计书 16.8，异步：下发带 testOnly 标记的预检指令，结果经指令回报）</summary>
    public async Task<TestRecognitionResponse> TestRecognitionAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.AsNoTracking()
            .Include(t => t.Client)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        EnsureClientDispatchable(task.Client);

        var command = await _commands.CreateCommandAsync(
            task.ClientId, CommandType.PrecheckTask,
            taskId: task.Id,
            payload: new { testOnly = true },
            priority: task.Priority,
            createdBy: _context.UserId,
            ct: ct);

        await _audit.RecordAsync("task.test_recognition", AuditResult.Success, "backup_task", task.Id,
            afterData: JsonSerializer.Serialize(new { command.Id }), ct: ct);

        return new TestRecognitionResponse { OperationId = command.Id };
    }

    // ---------- 私有辅助 ----------

    private void EnsureClientDispatchable(Core.Entities.Client.Client client)
    {
        if (client.Status == ClientStatus.PendingApproval)
            throw new BusinessException("CONFLICT", "客户端尚未审批通过，不能下发指令", 409);
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CONFLICT", "客户端已禁用或已注销，不能下发指令", 409);
    }

    /// <summary>判断当前是否处于任务上传窗口（按任务时区换算）</summary>
    private static bool IsWithinUploadWindow(BackupTask task)
    {
        if (task.UploadWindowStart is null || task.UploadWindowEnd is null)
            return true;

        TimeSpan localNow;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                string.IsNullOrWhiteSpace(task.ScheduleTimezone) ? "Asia/Shanghai" : task.ScheduleTimezone);
            localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).TimeOfDay;
        }
        catch (TimeZoneNotFoundException)
        {
            localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")).TimeOfDay;
        }

        var start = task.UploadWindowStart.Value;
        var end = task.UploadWindowEnd.Value;

        // 支持跨天窗口（如 22:00 - 06:00）
        return start <= end
            ? localNow >= start && localNow <= end
            : localNow >= start || localNow <= end;
    }

    /// <summary>应用可编辑字段（创建/更新共用，含参数校验）</summary>
    private void ApplyEditableFields(
        BackupTask task, string name, string applicationName, string sourcePath,
        string recognizerType, string taskMode, bool enabled, int priority,
        string importanceLevel, string? scanSchedule, string? uploadWindowStart, string? uploadWindowEnd,
        string scheduleTimezone, int randomDelayMinutes, int stabilityIntervalSeconds,
        int maxStabilityWaitSeconds, long? minTotalBytes, long? maxTotalBytes,
        int? minFileCount, int? bandwidthLimitKbps, int chunkSizeBytes,
        int retryCount, int retryIntervalSeconds, Guid? retentionPolicyId,
        string recognizerConfig, string? alertConfig)
    {
        if (!EnumMapping.TryParseSnakeCase<RecognizerType>(recognizerType, out var recognizer))
            throw new BusinessException("INVALID_REQUEST", $"无效的识别类型：{recognizerType}", 400);
        if (!EnumMapping.TryParseSnakeCase<TaskMode>(taskMode, out var mode))
            throw new BusinessException("INVALID_REQUEST", $"无效的任务模式：{taskMode}", 400);
        if (mode == TaskMode.Paused)
            throw new BusinessException("INVALID_REQUEST", "不能直接把任务创建/修改为暂停模式，请使用暂停接口", 400);
        if (!EnumMapping.TryParseSnakeCase<ImportanceLevel>(importanceLevel, out var importance))
            throw new BusinessException("INVALID_REQUEST", $"无效的重要等级：{importanceLevel}", 400);

        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationFailedException("任务名称必填");
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ValidationFailedException("备份源路径必填");

        if (chunkSizeBytes is < MinChunkSize or > MaxChunkSize)
            throw new BusinessException("INVALID_REQUEST",
                $"分块大小必须在 {MinChunkSize / 1024 / 1024}MB - {MaxChunkSize / 1024 / 1024}MB 之间", 400);

        if (minTotalBytes is not null && maxTotalBytes is not null && minTotalBytes > maxTotalBytes)
            throw new ValidationFailedException("总大小下限不能大于上限");

        if (stabilityIntervalSeconds < 60)
            throw new ValidationFailedException("稳定性判定间隔不能小于 60 秒");

        if (retentionPolicyId is not null &&
            !_db.RetentionPolicies.AsNoTracking().Any(p => p.Id == retentionPolicyId))
            throw new NotFoundException("保留策略", retentionPolicyId.Value);

        // 识别规则必须是合法 JSON
        if (!string.IsNullOrWhiteSpace(recognizerConfig))
        {
            try
            {
                using var _ = JsonDocument.Parse(recognizerConfig);
            }
            catch (JsonException)
            {
                throw new BusinessException("INVALID_REQUEST", "识别规则配置不是合法的 JSON", 400);
            }
        }

        task.Name = name.Trim();
        task.ApplicationName = applicationName.Trim();
        task.SourcePath = sourcePath.Trim();
        task.RecognizerType = recognizer;
        task.TaskMode = mode;
        task.Enabled = enabled;
        task.Priority = Math.Clamp(priority, 1, 1000);
        task.ImportanceLevel = importance;
        task.ScanSchedule = NormalizeScanSchedule(scanSchedule);
        task.UploadWindowStart = ParseTimeOfDay(uploadWindowStart, nameof(uploadWindowStart));
        task.UploadWindowEnd = ParseTimeOfDay(uploadWindowEnd, nameof(uploadWindowEnd));
        task.ScheduleTimezone = string.IsNullOrWhiteSpace(scheduleTimezone) ? "Asia/Shanghai" : scheduleTimezone.Trim();
        task.RandomDelayMinutes = Math.Clamp(randomDelayMinutes, 0, 1440);
        task.StabilityIntervalSeconds = stabilityIntervalSeconds;
        task.MaxStabilityWaitSeconds = Math.Max(maxStabilityWaitSeconds, stabilityIntervalSeconds);
        task.MinTotalBytes = minTotalBytes;
        task.MaxTotalBytes = maxTotalBytes;
        task.MinFileCount = minFileCount;
        task.BandwidthLimitKbps = bandwidthLimitKbps;
        task.ChunkSizeBytes = chunkSizeBytes;
        task.RetryCount = Math.Clamp(retryCount, 0, 10);
        task.RetryIntervalSeconds = Math.Max(retryIntervalSeconds, 60);
        task.RetentionPolicyId = retentionPolicyId;
        task.RecognizerConfig = string.IsNullOrWhiteSpace(recognizerConfig) ? "{}" : recognizerConfig;
        task.AlertConfig = string.IsNullOrWhiteSpace(alertConfig) ? null : alertConfig;
    }

    /// <summary>
    /// 校验并归一化扫描计划。cron 由 Agent 本地解析执行，写错了没有任何即时反馈——
    /// 只会表现为「到点没有扫描」，而界面上看不出错在哪一段。因此保存时就用
    /// Agent 使用的同一份解析器挡下来，把错误落在填表的人面前。
    /// </summary>
    private static string? NormalizeScanSchedule(string? scanSchedule)
    {
        if (string.IsNullOrWhiteSpace(scanSchedule))
            return null;

        var trimmed = scanSchedule.Trim();
        var error = CronExpression.Validate(trimmed);
        if (error is not null)
            throw new BusinessException("INVALID_REQUEST", $"扫描计划不是合法的 cron 表达式：{error}", 400);

        return trimmed;
    }

    private static TimeSpan? ParseTimeOfDay(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (TimeSpan.TryParse(value, out var parsed) && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
            return parsed;

        throw new BusinessException("INVALID_REQUEST", $"{fieldName} 格式应为 HH:mm（如 22:00）", 400);
    }
}
