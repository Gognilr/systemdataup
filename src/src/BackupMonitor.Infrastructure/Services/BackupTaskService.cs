using System.Text.Json;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Recognition;
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
    Task<DispatchCommandResponse> DispatchPrecheckAsync(Guid taskId, bool forceFullHash = false, CancellationToken ct = default);

    /// <summary>
    /// 一批任务的「立即备份」（D3）。建一次 Manual 执行交给顺序执行器按并发度放行，
    /// 而不是给每个任务各发一条独立指令——那样十个任务就是十个同时开传。
    /// </summary>
    Task<BatchBackupNowResponse> DispatchPrecheckBatchAsync(
        BatchBackupNowRequest request, CancellationToken ct = default);
    Task<DispatchCommandResponse> DispatchUploadAsync(Guid taskId, DispatchUploadRequest request, CancellationToken ct = default);
    Task<TestRecognitionResponse> TestRecognitionAsync(Guid taskId, CancellationToken ct = default);
    Task<CommandResultDto> GetCommandResultAsync(Guid commandId, CancellationToken ct = default);

    /// <summary>该任务最近一次识别测试的指令（含结果）；从未测过返回 null。</summary>
    Task<CommandResultDto?> GetLatestRecognitionTestAsync(Guid taskId, CancellationToken ct = default);
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

    private static readonly UploadStatus[] ActiveUploadStatuses = UploadSessionStatuses.InFlight;

    /// <summary>
    /// B5/B4：最近预检状态里"不算问题"的取值——通过、没有新备份、还没扫描过（含从未扫描）。
    /// 待办页与 GetListAsync(OnlyProblematic) 共用同一份判定，不再各写一份（此前浏览器里
    /// 用黑名单 ['failed','failure','error']，漏掉了 path_not_found / required_file_missing 等）。
    /// </summary>
    internal static readonly PrecheckStatus[] OkPrecheckStatuses =
        [PrecheckStatus.Passed, PrecheckStatus.NoNewBackup, PrecheckStatus.NotScanned];

    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _commands;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly SystemSettingsProvider _settings;
    private readonly IExecutionQueueService _queue;

    /// <summary>删除任务时要清掉回收站里那些备份集的仓库目录，需要仓库根做围栏</summary>
    private readonly IUploadStorage _storage;

    private readonly ILogger<BackupTaskService> _logger;

    public BackupTaskService(
        AppDbContext db,
        ICommandDispatcher commands,
        ICurrentContext context,
        IAuditRecorder audit,
        SystemSettingsProvider settings,
        IExecutionQueueService queue,
        IUploadStorage storage,
        ILogger<BackupTaskService> logger)
    {
        _db = db;
        _commands = commands;
        _context = context;
        _audit = audit;
        _settings = settings;
        _queue = queue;
        _storage = storage;
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

        // 不选保留策略就真的留 NULL。V027 曾在这里把默认策略 ID 抄一份写进任务，那是为了堵住
        // 「留空 = 永不清理」这个反向默认值；现在清理器读到 NULL 会自己回落到默认策略（F2），
        // 兜底拷贝就没必要了——而且它有害：拷进去之后再改默认策略，存量任务纹丝不动，
        // 「默认」变成了建任务那一刻的一次性快照。留 NULL 才能让默认策略持续生效。
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

    /// <summary>
    /// 查询一条指令的执行状态与结果。识别测试是异步的——下发之后界面要能轮询它跑完没有。
    /// </summary>
    public async Task<CommandResultDto> GetCommandResultAsync(Guid commandId, CancellationToken ct = default)
    {
        var command = await _db.Commands.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commandId, ct)
            ?? throw new NotFoundException("指令", commandId);

        return ToCommandResultDto(command);
    }

    private static CommandResultDto ToCommandResultDto(Core.Entities.Backup.Command command) => new()
    {
        CommandId = command.Id,
        CommandType = EnumMapping.ToSnakeCase(command.CommandType),
        Status = EnumMapping.ToSnakeCase(command.Status),
        TaskId = command.TaskId,
        CreatedAt = command.CreatedAt,
        StartedAt = command.StartedAt,
        CompletedAt = command.CompletedAt,
        ResultCode = command.ResultCode,
        ResultMessage = command.ResultMessage,
        ResultPayload = command.ResultPayload
    };

    public async Task DeleteAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        // 只有「还活着」的备份版本才挡删除。回收站里的和已彻底删除的行仍然在表里，
        // 拿它们挡住删除，等于任何成功备份过一次的任务永远删不掉——
        // 而原先的提示「请先处理保留策略」指向的是一条走不通的路：
        // 清空回收站、物理删除之后这些行照样在。
        var hasLiveSets = await _db.BackupSets.AnyAsync(
            s => s.TaskId == taskId && BackupSetStatuses.Live.Contains(s.Status), ct);
        if (hasLiveSets)
            throw new BusinessException("CONFLICT",
                "任务下还有可用的备份版本。请先在「备份集」页面把它们删除（会进回收站），再删除任务。", 409);

        var hasActiveSessions = await _db.UploadSessions.AnyAsync(s =>
            s.TaskId == taskId && ActiveUploadStatuses.Contains(s.Status), ct);
        if (hasActiveSessions)
            throw new BusinessException("CONFLICT", "任务存在正在进行的上传，等它结束或先取消再删除", 409);

        // 原先这里还有两道守卫：「存在候选备份集」和「存在历史上传会话记录」。
        // 两条都没有可满足的路径——候选备份集全系统没有任何删除入口，
        // 历史会话是永久保留的。它们和上面那条一起，让任何跑过一次的任务永远删不掉。
        // 候选、历史会话、已删除的备份集都是任务的附属记录，任务没了它们也失去意义，
        // 与下面已有的 commands / alerts 一样随任务一并清理。

        var deleted = await CascadeDeleteTaskDependentsAsync(taskId, ct);

        _db.BackupTasks.Remove(task);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("task.delete", AuditResult.Success, "backup_task", taskId,
            beforeData: JsonSerializer.Serialize(new
            {
                task.Id,
                task.Name,
                task.ClientId,
                deleted.RemovedCommands,
                deleted.RemovedAlerts,
                deleted.CancelledDeliveries,
                deleted.RemovedBackupSets,
                deleted.RemovedCandidates,
                deleted.RemovedUploadSessions,
                deleted.RemovedRestoreRequests,
                deleted.RemovedExecutionItems,
                deleted.RemovedRepositoryDirectories
            }), ct: ct);
    }

    /// <summary>删除任务时一并清掉的附属记录数量，落进审计日志。</summary>
    private readonly record struct TaskCascadeResult(
        int RemovedCommands,
        int RemovedAlerts,
        int CancelledDeliveries,
        int RemovedBackupSets,
        int RemovedCandidates,
        int RemovedUploadSessions,
        int RemovedRestoreRequests,
        int RemovedExecutionItems,
        int RemovedRepositoryDirectories);

    /// <summary>
    /// 级联清理任务的全部附属记录。
    ///
    /// 顺序由外键决定，不能调整：这些表之间大多是 ON DELETE NO ACTION，
    /// 顺序错了不会得到一句「顺序不对」，而是一条外键冲突异常，
    /// 前端看到的是「服务器内部错误」——正是这条缺陷原来的表现。
    /// 每一步都说明它为什么必须在这个位置。
    /// </summary>
    private async Task<TaskCascadeResult> CascadeDeleteTaskDependentsAsync(Guid taskId, CancellationToken ct)
    {
        // 走到这里剩下的备份集只可能是 recycle_bin / deleted（活着的已被守卫挡住）。
        // 先取出来：物理目录要在删行之前清掉，删完行就再也不知道路径了。
        var setIds = await _db.BackupSets
            .Where(s => s.TaskId == taskId)
            .Select(s => new { s.Id, s.Status, s.RepositoryPath, s.BackupSetCode })
            .ToListAsync(ct);

        var unitIds = await _db.BusinessUnits
            .Where(u => u.TaskId == taskId)
            .Select(u => u.Id)
            .ToListAsync(ct);

        // 一、通知与告警。必须在删备份集/业务单元之前：alerts.backup_set_id 与
        // alerts.business_unit_id 都是 NO ACTION，留着它们就删不掉被引用的行。
        // 而删告警之前要先取消它下面还没发出去的投递——notification_deliveries.alert_id
        // 是「告警删除后置空」，删完告警这些投递会变成孤儿 pending 记录，
        // 派发器只看投递自身的状态，于是任务都删掉了，它的告警邮件还在继续重试发送。
        // 这两步的顺序也不能反：删完告警就查不到 task_id 了。
        var cancelledDeliveries = await _db.NotificationDeliveries
            .Where(d => d.Alert != null
                        && (d.Alert.TaskId == taskId
                            || (d.Alert.BackupSetId != null && setIds.Select(s => s.Id).Contains(d.Alert.BackupSetId.Value))
                            || (d.Alert.BusinessUnitId != null && unitIds.Contains(d.Alert.BusinessUnitId.Value)))
                        && d.Status == NotificationStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, NotificationStatus.Cancelled), ct);

        var removedAlerts = await _db.Alerts
            .Where(a => a.TaskId == taskId
                || (a.BackupSetId != null && setIds.Select(s => s.Id).Contains(a.BackupSetId.Value))
                || (a.BusinessUnitId != null && unitIds.Contains(a.BusinessUnitId.Value)))
            .ExecuteDeleteAsync(ct);

        // 二、指令。commands.task_id 与 commands.candidate_backup_set_id 都是 NO ACTION，
        // 必须在候选之前删掉。原先只是把 pending 指令改成 cancelled——行还在，
        // 于是任何点过一次预检的任务都会在删除时触发外键冲突。
        var removedCommands = await _db.Commands
            .Where(c => c.TaskId == taskId)
            .ExecuteDeleteAsync(ct);

        // 三、执行队列项。execution_run_items.task_id 本身是 CASCADE，但它同时引用候选和会话
        // （SET NULL），显式先删掉，免得依赖两条级联规则的相对顺序。
        var removedExecutionItems = await _db.ExecutionRunItems
            .Where(i => i.TaskId == taskId)
            .ExecuteDeleteAsync(ct);

        // 四、恢复请求。restore_requests.backup_set_id 是 NO ACTION，
        // 必须在备份集之前删。EnsureRemovable 那类守卫只挡住「正在恢复」的，
        // 历史上完成过的恢复请求会一直留着——不清它，删任务照样撞外键。
        var setIdList = setIds.Select(s => s.Id).ToList();
        var removedRestoreRequests = setIdList.Count == 0
            ? 0
            : await _db.RestoreRequests
                .Where(r => setIdList.Contains(r.BackupSetId))
                .ExecuteDeleteAsync(ct);

        // 五、物理目录。回收站里的备份集目录还在磁盘上，删完行就没人知道它的路径了，
        // 只能等 RepositoryReconcileWorker 事后把它报成孤儿目录。
        var removedDirectories = 0;
        var recyclable = setIds.Where(s => s.Status == BackupSetStatus.RecycleBin
            && !string.IsNullOrWhiteSpace(s.RepositoryPath)).ToList();
        if (recyclable.Count > 0)
        {
            var repositoryRoot = await _storage.GetRepositoryRootAsync(ct);
            foreach (var s in recyclable)
            {
                if (RepositoryDirectory.DeleteUnderRoot(s.RepositoryPath, repositoryRoot))
                    removedDirectories++;
                else
                    _logger.LogWarning("删除任务时备份集 {Code} 的仓库目录不存在，只做逻辑删除：{Path}",
                        s.BackupSetCode, s.RepositoryPath);
            }
        }

        // 六、备份集。backup_files 与 retention_locks 是 CASCADE，随它一起走。
        // 必须在候选与上传会话之前：backup_sets 同时引用这两者（NO ACTION）。
        var removedBackupSets = await _db.BackupSets
            .Where(s => s.TaskId == taskId)
            .ExecuteDeleteAsync(ct);

        // 七、上传会话。upload_files → upload_chunks 都是 CASCADE。
        // 必须在候选之前：upload_sessions.candidate_backup_set_id 是 NOT NULL 的 NO ACTION。
        var removedUploadSessions = await _db.UploadSessions
            .Where(s => s.TaskId == taskId)
            .ExecuteDeleteAsync(ct);

        // 八、候选备份集。candidate_files 是 CASCADE；superseded_by_id 是同表自引用，
        // 一条 DELETE 里一起删掉不会互相挡住。
        var removedCandidates = await _db.CandidateBackupSets
            .Where(c => c.TaskId == taskId)
            .ExecuteDeleteAsync(ct);

        // business_units.task_id 是 ON DELETE CASCADE，随任务自己走——
        // 但引用它的候选与备份集必须先清掉（上面两步），否则这条级联会被外键挡住。

        return new TaskCascadeResult(
            removedCommands,
            removedAlerts,
            cancelledDeliveries,
            removedBackupSets,
            removedCandidates,
            removedUploadSessions,
            removedRestoreRequests,
            removedExecutionItems,
            removedDirectories);
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

        if (!string.IsNullOrWhiteSpace(query.PrecheckStatus))
        {
            var values = query.PrecheckStatus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(raw =>
                {
                    if (!EnumMapping.TryParseSnakeCase<PrecheckStatus>(raw, out var parsed))
                        throw new BusinessException("INVALID_REQUEST", $"无效的预检状态：{raw}", 400);
                    return parsed;
                })
                .ToList();
            if (values.Count > 0)
                tasks = tasks.Where(t => t.LastPrecheckStatus != null && values.Contains(t.LastPrecheckStatus.Value));
        }

        if (query.OnlyProblematic is true)
        {
            var now = DateTime.UtcNow;
            var scanStaleCutoff = now.AddHours(-24);
            var successStaleCutoff = now.AddDays(-3);
            tasks = tasks.Where(t => t.Enabled && (
                (t.LastPrecheckStatus != null && !OkPrecheckStatuses.Contains(t.LastPrecheckStatus.Value))
                || (t.LastScanAt != null && t.LastScanAt < scanStaleCutoff
                    && (t.LastSuccessAt == null || t.LastSuccessAt < successStaleCutoff))));
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
                ClientDisplayName = t.Client.DisplayName,
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
            ClientDisplayName = r.ClientDisplayName,
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

        // 属于某个备份计划时，扫描计划不再由客户端触发（AgentConfigService 会把它置空）
        var planMembership = await _db.BackupPlanItems.AsNoTracking()
            .Include(i => i.Plan)
            .FirstOrDefaultAsync(i => i.TaskId == taskId, ct);

        return new BackupTaskDetailDto
        {
            Id = task.Id,
            ClientId = task.ClientId,
            ClientHostname = task.Client.Hostname,
            ClientDisplayName = task.Client.DisplayName,
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

            PreviousTaskMode = task.PreviousTaskMode.HasValue
                ? EnumMapping.ToSnakeCase(task.PreviousTaskMode.Value) : null,
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
            PlanId = planMembership?.PlanId,
            PlanName = planMembership?.Plan.Name,
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

        task.TaskMode = task.PreviousTaskMode ?? TaskMode.Automatic;
        task.PreviousTaskMode = null;
        task.ConfigVersion++;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("task.resume", AuditResult.Success, "backup_task", task.Id, ct: ct);

        return await GetDetailAsync(task.Id, ct);
    }

    /// <summary>
    /// 手动下发预检（设计书 16.6，「立即备份」按钮走的就是这条）。
    ///
    /// 幂等键固定成 manual-precheck:{taskId}，语义与识别测试（TestRecognitionAsync）对齐：
    /// 一个任务同一时刻只该有一条手动预检指令。
    /// - 上一条还在 pending/claimed/running：原样还回去，响应带 already_running，
    ///   界面接着轮询那一条，而不是装作新发起了一次；
    /// - 上一条已经终结（成功/失败/取消/过期）：复位重下（restartIfNotActive）。
    ///
    /// 原先键里拼了当前秒，只挡得住同一秒内的重复提交——扫描要跑几分钟，
    /// 误关等待窗口后再点一次就是一条全新指令，点几次就并发几条。
    /// 反过来，只认「键存在就拒绝」也不行：那会让任务在第一次备份之后永远点不动，
    /// 所以终结态必须允许重下。
    /// </summary>
    public async Task<DispatchCommandResponse> DispatchPrecheckAsync(Guid taskId, bool forceFullHash = false, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.AsNoTracking()
            .Include(t => t.Client)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        EnsureClientDispatchable(task.Client);

        var command = await _commands.CreateCommandAsync(
            task.ClientId, CommandType.PrecheckTask,
            taskId: task.Id,
            // forceFullHash：跳过 Agent 的快速指纹基线，无条件重算全量 SHA-256。
            // 定期做一次完整校验是有意义的，但它应该是人明确要求的动作，不是默认每次都做。
            payload: forceFullHash ? new { forceFullHash = true } : null,
            priority: task.Priority,
            createdBy: _context.UserId,
            idempotencyKey: ManualPrecheckKey(task.Id),
            restartIfNotActive: true,
            ct: ct);

        return new DispatchCommandResponse
        {
            CommandId = command.Id,
            Status = command.Status is CommandStatus.Claimed or CommandStatus.Running
                ? "already_running"
                : "accepted"
        };
    }

    private static string ManualPrecheckKey(Guid taskId) => $"manual-precheck:{taskId:N}";

    /// <summary>
    /// 批量「立即备份」（D3）。单个任务点一次仍然走 DispatchPrecheckAsync 保持即时反馈
    /// （重复点击由 D1 的在途去重与稳定幂等键挡住）；多选才建执行，
    /// 因为「十个任务同时开传」正是要消灭的那个行为。
    ///
    /// 一个任务都建不起来（全都停用/暂停/客户端不可用）时退回逐个下发，
    /// 让每个任务各自的失败原因照常回到调用方，而不是笼统一句「没建成」。
    /// </summary>
    public async Task<BatchBackupNowResponse> DispatchPrecheckBatchAsync(
        BatchBackupNowRequest request, CancellationToken ct = default)
    {
        var taskIds = (request.TaskIds ?? []).Distinct().ToList();
        if (taskIds.Count == 0)
            throw new BusinessException("INVALID_REQUEST", "没有选中任何任务", 400);

        var run = await _queue.CreateManualRunAsync(taskIds, _context.UserId, ct);
        if (run is not null)
        {
            return new BatchBackupNowResponse
            {
                ExecutionRunId = run.Id,
                QueuedTasks = run.Items.Count(i => i.Status == ExecutionItemStatus.Pending),
                SkippedTasks = run.Items.Count(i => i.Status == ExecutionItemStatus.Skipped),
                MaxConcurrent = run.MaxConcurrent
            };
        }

        var dispatched = 0;
        foreach (var taskId in taskIds)
        {
            await DispatchPrecheckAsync(taskId, forceFullHash: false, ct);
            dispatched++;
        }

        return new BatchBackupNowResponse { QueuedTasks = dispatched, MaxConcurrent = dispatched };
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

        // 状态过滤不能省：删掉的备份集行仍在表里，不过滤就是「删了再也备不回来」
        var archived = await _db.BackupSets.AnyAsync(
            s => s.SourceCandidateId == candidate.Id && BackupSetStatuses.Live.Contains(s.Status), ct);
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

        // 如实说明这一次到底发生了什么。原先固定不填 Status，界面于是无论如何都说
        // 「已开始上传」——包括「幂等键命中了一条还在跑/早就跑完的旧指令，这次什么都没发」
        // 那一种，而那正是「说传了、传输中却永远是空的」的来源之一。
        return new DispatchCommandResponse
        {
            CommandId = command.Id,
            Status = command.Status switch
            {
                CommandStatus.Pending => "accepted",
                CommandStatus.Claimed or CommandStatus.Running => "already_running",
                CommandStatus.Succeeded => "already_done",
                _ => "not_dispatched"
            }
        };
    }

    /// <summary>
    /// 识别测试（设计书 16.8，异步：下发带 testOnly 标记的预检指令，结果经指令回报）。
    ///
    /// 幂等键固定成 test-recognition:{taskId}，一个任务永远只有一条识别测试指令：
    /// - 上一条还在排队/执行中：直接把它还回去，而不是再堆一条排在它后面
    ///   （原先每点一次「重新测试」就多一条，全都排在那条没跑完的后面，越点越慢）；
    /// - 上一条已经跑完：复位重下，同时这一行就是「最近一次识别测试」的落点，
    ///   界面关掉对话框之后照样查得到（GetLatestRecognitionTestAsync）。
    /// </summary>
    public async Task<TestRecognitionResponse> TestRecognitionAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.BackupTasks.AsNoTracking()
            .Include(t => t.Client)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("备份任务", taskId);

        EnsureClientDispatchable(task.Client);
        await EnsureClientAliveAsync(task.Client, ct);

        var command = await _commands.CreateCommandAsync(
            task.ClientId, CommandType.PrecheckTask,
            taskId: task.Id,
            payload: new { testOnly = true },
            // 优先级压过常规指令（越小越优先）：Agent 的指令是串行执行的，
            // 识别测试排在一条上传后面就是几分钟起步，而人正对着对话框等。
            // 插队的代价很小——试扫不算哈希、不写任何东西，通常一秒内就跑完；
            // 注意它只能插到「还没被领走」的指令前面，正在跑的那条仍然要等它跑完。
            priority: 10,
            idempotencyKey: RecognitionTestKey(task.Id),
            createdBy: _context.UserId,
            restartIfNotActive: true,
            ct: ct);

        await _audit.RecordAsync("task.test_recognition", AuditResult.Success, "backup_task", task.Id,
            afterData: JsonSerializer.Serialize(new { command.Id }), ct: ct);

        return new TestRecognitionResponse
        {
            OperationId = command.Id,
            Status = command.Status is CommandStatus.Claimed or CommandStatus.Running
                ? "already_running"
                : "accepted"
        };
    }

    /// <summary>
    /// 最近一次识别测试的指令（含结果）。识别测试的等待窗口只有一分钟，
    /// 而人关掉对话框之后 Agent 照样会把它跑完——没有这个查询，那次结果就等于白跑了。
    /// </summary>
    public async Task<CommandResultDto?> GetLatestRecognitionTestAsync(Guid taskId, CancellationToken ct = default)
    {
        var key = RecognitionTestKey(taskId);
        var command = await _db.Commands.AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdempotencyKey == key, ct);

        return command is null ? null : ToCommandResultDto(command);
    }

    private static string RecognitionTestKey(Guid taskId) => $"test-recognition:{taskId:N}";

    // ---------- 私有辅助 ----------

    private void EnsureClientDispatchable(Core.Entities.Client.Client client)
    {
        if (client.Status == ClientStatus.PendingApproval)
            throw new BusinessException("CONFLICT", "客户端尚未审批通过，不能下发指令", 409);
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CONFLICT", "客户端已禁用或已注销，不能下发指令", 409);
    }

    /// <summary>
    /// 只给「人在界面前等结果」的动作用（识别测试）：客户端已经不发心跳了就当场说清楚，
    /// 而不是把指令挂进队列、让人对着进度条等满一分钟再去猜是不是离线。
    ///
    /// 阈值取系统设置里的 client_offline_threshold_seconds，与存活巡检同一个值——
    /// 各写各的判定，迟早会出现「客户端列表显示在线、这里说它离线」。
    /// 状态本身也要看：巡检可能还没跑到，也可能刚跑完，两个信号哪个先到都算数。
    /// 计划扫描、立即备份这类不需要即时反馈的照旧不拦——指令有 24 小时 TTL，
    /// 客户端回来以后接着执行才是对的。
    /// </summary>
    private async Task EnsureClientAliveAsync(Core.Entities.Client.Client client, CancellationToken ct)
    {
        var offlineAfter = Math.Max(1, await _settings.GetIntAsync(
            SystemWatchdogWorker.OfflineThresholdKey, 300, ct));
        var lastSeen = client.LastHeartbeatAt ?? client.ApprovedAt ?? client.CreatedAt;
        var silence = (DateTime.UtcNow - lastSeen).TotalSeconds;

        if (client.Status != ClientStatus.Offline && silence < offlineAfter)
            return;

        var detail = client.LastHeartbeatAt is null
            ? "它从未上报过心跳"
            : $"最近一次心跳是 {client.LastHeartbeatAt:yyyy-MM-dd HH:mm:ss} UTC";
        throw new BusinessException("CLIENT_OFFLINE",
            $"客户端「{client.DisplayName}」当前离线，{detail}。等它恢复连接后再测试。", 409);
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

            // 合法 JSON 不等于有效规则。识别规则的解析对不认识的键一律静默忽略，
            // 于是把 requiredFiles 敲成 requireFiles 的后果是：保存成功、界面正常、
            // 规则完全失效——直到某天真要恢复数据时才发现。这种错误必须在保存时挡住。
            var unknownKeys = RecognizerRules.FindUnknownKeys(recognizerConfig);
            if (unknownKeys.Count > 0)
            {
                var details = unknownKeys.Select(key =>
                {
                    var suggestion = RecognizerRules.SuggestKey(key);
                    return suggestion is null ? key : $"{key}（是不是想写 {suggestion}？）";
                });
                throw new BusinessException(
                    "INVALID_REQUEST",
                    $"识别规则里有无法识别的配置项：{string.Join("、", details)}。"
                    + $"可用字段：{string.Join(" / ", RecognizerRules.KnownKeys)}",
                    400);
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
