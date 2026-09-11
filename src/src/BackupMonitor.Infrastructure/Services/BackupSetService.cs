using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Retention;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>管理端备份查询服务（设计书 18 备份查询接口）</summary>
public interface IBackupSetService
{
    Task<PagedResult<BackupSetListItemDto>> GetListAsync(BackupQuery query, CancellationToken ct = default);

    /// <summary>
    /// 同样的筛选条件，但按「客户端 + 任务」归拢成一行一组。
    /// 展开某一组时前端再带 taskId 调 GetListAsync 拿这一组的明细。
    /// </summary>
    Task<PagedResult<BackupSetGroupDto>> GetGroupsAsync(BackupQuery query, CancellationToken ct = default);
    Task<BackupSetDetailDto> GetDetailAsync(Guid backupSetId, CancellationToken ct = default);
    Task<List<BackupFileDto>> GetFilesAsync(Guid backupSetId, CancellationToken ct = default);
    Task LockAsync(Guid backupSetId, LockBackupRequest request, CancellationToken ct = default);
    Task UnlockAsync(Guid backupSetId, CancellationToken ct = default);
    Task<VerifyBackupResponse> VerifyAsync(Guid backupSetId, CancellationToken ct = default);

    /// <summary>隔离（D1）：人工怀疑该备份有问题时标记，不参与保留计算、不能用于恢复，但也不删除</summary>
    Task QuarantineAsync(Guid backupSetId, QuarantineBackupRequest request, CancellationToken ct = default);

    /// <summary>解除隔离，恢复为可用状态</summary>
    Task UnquarantineAsync(Guid backupSetId, CancellationToken ct = default);

    /// <summary>
    /// 删除（D）：移入回收站而不是直接物理删除，误删还能捞回来。
    /// 守卫与保留清理完全一致——已锁定 / 有活动保留锁 / 有进行中的恢复请求，一律拒绝。
    /// </summary>
    Task RecycleAsync(Guid backupSetId, CancellationToken ct = default);

    /// <summary>从回收站还原为可用</summary>
    Task RestoreFromRecycleBinAsync(Guid backupSetId, CancellationToken ct = default);

    /// <summary>立即彻底删除（只能对回收站里的备份集执行，物理删除仓库目录，不可撤销）</summary>
    Task PurgeAsync(Guid backupSetId, CancellationToken ct = default);

    /// <summary>批量移入回收站</summary>
    Task<BackupSetBatchResult> RecycleBatchAsync(BackupSetBatchRequest request, CancellationToken ct = default);

    /// <summary>批量从回收站还原</summary>
    Task<BackupSetBatchResult> RestoreFromRecycleBinBatchAsync(BackupSetBatchRequest request, CancellationToken ct = default);

    /// <summary>批量彻底删除（不可撤销）</summary>
    Task<BackupSetBatchResult> PurgeBatchAsync(BackupSetBatchRequest request, CancellationToken ct = default);
}

/// <summary>备份查询实现（列表/详情/文件/锁定/解锁/重校验）</summary>
public class BackupSetService : IBackupSetService
{

    // ── 列表排序白名单（审查 P1-3）。备份集按大小/时间排序是排障最常用的两种视角。
    private static readonly Dictionary<string, Func<IQueryable<BackupSet>, bool, IQueryable<BackupSet>>> BackupSetSorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["backupSetCode"] = SortWhitelist.By<BackupSet, string>(s => s.BackupSetCode),
            ["status"] = SortWhitelist.By<BackupSet, BackupSetStatus>(s => s.Status),
            ["totalBytes"] = SortWhitelist.By<BackupSet, long>(s => s.TotalBytes),
            ["totalFiles"] = SortWhitelist.By<BackupSet, int>(s => s.TotalFiles),
            ["backupBusinessTime"] = SortWhitelist.By<BackupSet, DateTime?>(s => s.BackupBusinessTime),
            ["uploadedAt"] = SortWhitelist.By<BackupSet, DateTime?>(s => s.UploadedAt),
            ["retentionUntil"] = SortWhitelist.By<BackupSet, DateTime?>(s => s.RetentionUntil)
        };

    private static readonly Func<IQueryable<BackupSet>, bool, IQueryable<BackupSet>> BackupSetSortFallback =
        SortWhitelist.By<BackupSet, DateTime?>(s => s.UploadedAt);
    private readonly AppDbContext _db;
    private readonly Channel<WorkItem> _workChannel;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly IUploadStorage _storage;
    private readonly ILogger<BackupSetService> _logger;

    public BackupSetService(
        AppDbContext db,
        [FromKeyedServices(QueueKeys.Verify)] Channel<WorkItem> workChannel,
        ICurrentContext context,
        IAuditRecorder audit,
        IUploadStorage storage,
        ILogger<BackupSetService> logger)
    {
        _db = db;
        _workChannel = workChannel;
        _context = context;
        _audit = audit;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// 列表与归拢视图共用的筛选。抽出来是因为两边一旦分家，
    /// 归拢行上的「18 份」和展开后数出来的行数就会对不上，
    /// 而那种对不上没有任何办法从界面上看出是筛选口径的差异还是真的少了备份。
    /// </summary>
    private IQueryable<BackupSet> FilterSets(BackupQuery query)
    {
        var sets = _db.BackupSets.AsNoTracking().AsQueryable();

        if (query.ClientId is not null)
            sets = sets.Where(s => s.ClientId == query.ClientId);
        if (query.TaskId is not null)
            sets = sets.Where(s => s.TaskId == query.TaskId);
        if (query.BusinessUnitId is not null)
            sets = sets.Where(s => s.BusinessUnitId == query.BusinessUnitId);
        if (!string.IsNullOrWhiteSpace(query.ApplicationName))
            sets = sets.Where(s => EF.Functions.ILike(s.Task.ApplicationName, $"%{query.ApplicationName.Trim()}%"));

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!EnumMapping.TryParseSnakeCase<BackupSetStatus>(query.Status, out var status))
                throw new BusinessException("INVALID_REQUEST", $"无效的备份状态：{query.Status}", 400);
            sets = sets.Where(s => s.Status == status);
        }
        else
        {
            // 不指定状态时只查还活着的。
            //
            // 已删除的行必须留在表里——restore_requests 和 alerts 对 backup_sets 都是
            // Restrict，物理删行等于要先删掉恢复历史和告警历史，而「这份备份被谁在什么
            // 时候恢复过」恰恰是最不该丢的记录。留着也没有副作用：deleted 不在
            // BackupSetStatuses.Live 里，不占「这个候选已入过库」的位置。
            //
            // 但它们不该混进默认视图，原因不是碍眼而是算数：归拢行的份数和容量是把
            // 筛出来的行全加起来的，已删除的备份磁盘上早就没了却照样进 TotalBytes——
            // 一个一份都不剩的任务，界面上报的是「36 份备份 18.11 GB」。
            // 要查已删除/回收站的，从状态那一档显式挑，那时人问的才是「那一份哪去了」。
            sets = sets.Where(s => BackupSetStatuses.Live.Contains(s.Status));
        }

        if (query.From is not null)
            sets = sets.Where(s => s.UploadedAt >= query.From);
        if (query.To is not null)
            sets = sets.Where(s => s.UploadedAt <= query.To);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
            sets = sets.Where(s => EF.Functions.ILike(s.BackupSetCode, $"%{query.Keyword.Trim()}%"));

        return sets;
    }

    public async Task<PagedResult<BackupSetGroupDto>> GetGroupsAsync(
        BackupQuery query, CancellationToken ct = default)
    {
        // 客户端名、任务名、应用名都由分组键函数决定，一起放进 GROUP BY 就不必再回表查一次。
        var groups = FilterSets(query)
            .GroupBy(s => new
            {
                s.ClientId,
                ClientHostname = s.Client.Hostname,
                s.TaskId,
                TaskName = s.Task.Name,
                ApplicationName = s.Task.ApplicationName
            });

        var totalCount = await groups.LongCountAsync(ct);

        var rows = await groups
            // 最新的备份排最前：这张表是用来看「还在不在出备份」的，
            // 最久没动静的那一组反而要靠翻到最后才看得见——所以倒序而不是正序。
            .OrderByDescending(g => g.Max(x => x.BackupBusinessTime ?? x.UploadedAt))
            .ThenBy(g => g.Key.TaskName)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(g => new
            {
                g.Key.ClientId,
                g.Key.ClientHostname,
                g.Key.TaskId,
                g.Key.TaskName,
                g.Key.ApplicationName,
                BackupSetCount = g.Count(),
                // 单元数用 COUNT(DISTINCT business_unit_id)。单元为 null 的（单单元任务、
                // 老数据）在 SQL 里不计入 distinct 计数，下面补成至少 1。
                BusinessUnitCount = g.Select(x => x.BusinessUnitId).Distinct().Count(),
                LatestBusinessTime = g.Max(x => x.BackupBusinessTime),
                LatestUploadedAt = g.Max(x => x.UploadedAt),
                TotalBytes = g.Sum(x => x.TotalBytes),
                AvailableCount = g.Count(x => x.Status == BackupSetStatus.Available),
                LockedCount = g.Count(x => x.Locked)
            })
            .ToListAsync(ct);

        var items = rows.Select(g => new BackupSetGroupDto
        {
            ClientId = g.ClientId,
            ClientHostname = g.ClientHostname,
            TaskId = g.TaskId,
            TaskName = g.TaskName,
            ApplicationName = g.ApplicationName,
            BackupSetCount = g.BackupSetCount,
            BusinessUnitCount = Math.Max(1, g.BusinessUnitCount),
            LatestBusinessTime = g.LatestBusinessTime,
            LatestUploadedAt = g.LatestUploadedAt,
            TotalBytes = g.TotalBytes,
            AvailableCount = g.AvailableCount,
            NotAvailableCount = g.BackupSetCount - g.AvailableCount,
            LockedCount = g.LockedCount
        }).ToList();

        return PagedResult<BackupSetGroupDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<PagedResult<BackupSetListItemDto>> GetListAsync(BackupQuery query, CancellationToken ct = default)
    {
        var sets = FilterSets(query);

        var totalCount = await sets.LongCountAsync(ct);

        var rows = await sets
            .ApplySort(query.SortBy, query.SortDescending, BackupSetSorts, BackupSetSortFallback)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(s => new
            {
                s.Id,
                s.BackupSetCode,
                s.ClientId,
                ClientHostname = s.Client.Hostname,
                s.TaskId,
                TaskName = s.Task.Name,
                ApplicationName = s.Task.ApplicationName,
                s.BusinessUnitId,
                BusinessUnitName = s.BusinessUnit != null ? s.BusinessUnit.DisplayName : null,
                s.Status,
                s.VerifyingSince,
                s.BackupBusinessTime,
                s.UploadedAt,
                s.TotalFiles,
                s.TotalBytes,
                s.Locked,
                s.RetentionUntil,
                s.SizeSuspicious,
                s.SizeSuspicionReason
            })
            .ToListAsync(ct);

        var items = rows.Select(s => new BackupSetListItemDto
        {
            Id = s.Id,
            BackupSetCode = s.BackupSetCode,
            ClientId = s.ClientId,
            ClientHostname = s.ClientHostname,
            TaskId = s.TaskId,
            TaskName = s.TaskName,
            ApplicationName = s.ApplicationName,
            BusinessUnitId = s.BusinessUnitId,
            BusinessUnitName = s.BusinessUnitName,
            Status = EnumMapping.ToSnakeCase(s.Status),
            VerifyingSince = s.VerifyingSince,
            BackupBusinessTime = s.BackupBusinessTime,
            UploadedAt = s.UploadedAt,
            TotalFiles = s.TotalFiles,
            TotalBytes = s.TotalBytes,
            Locked = s.Locked,
            RetentionUntil = s.RetentionUntil,
            SizeSuspicious = s.SizeSuspicious,
            SizeSuspicionReason = s.SizeSuspicionReason
        }).ToList();

        return PagedResult<BackupSetListItemDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<BackupSetDetailDto> GetDetailAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var set = await _db.BackupSets.AsNoTracking()
            .Include(s => s.Client)
            .Include(s => s.Task)
            .Include(s => s.BusinessUnit)
            .Include(s => s.Files)
            .Include(s => s.RetentionLocks).ThenInclude(l => l.LockedByUser)
            .FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        return new BackupSetDetailDto
        {
            Id = set.Id,
            BackupSetCode = set.BackupSetCode,
            ClientId = set.ClientId,
            ClientHostname = set.Client.Hostname,
            TaskId = set.TaskId,
            TaskName = set.Task.Name,
            ApplicationName = set.Task.ApplicationName,
            BusinessUnitId = set.BusinessUnitId,
            BusinessUnitName = set.BusinessUnit?.DisplayName,
            Status = EnumMapping.ToSnakeCase(set.Status),
            VerifyingSince = set.VerifyingSince,
            BackupBusinessTime = set.BackupBusinessTime,
            UploadedAt = set.UploadedAt,
            TotalFiles = set.TotalFiles,
            TotalBytes = set.TotalBytes,
            Locked = set.Locked,
            RetentionUntil = set.RetentionUntil,
            SizeSuspicious = set.SizeSuspicious,
            SizeSuspicionReason = set.SizeSuspicionReason,

            SourceCandidateId = set.SourceCandidateId,
            UploadSessionId = set.UploadSessionId,
            DiscoveredAt = set.DiscoveredAt,
            VerifiedAt = set.VerifiedAt,
            RepositoryPath = set.RepositoryPath,
            ManifestPath = set.ManifestPath,
            ManifestSha256 = set.ManifestSha256,
            CreatedAt = set.CreatedAt,

            Files = set.Files.OrderBy(f => f.RelativePath).Select(MapFile).ToList(),
            RetentionLocks = set.RetentionLocks.OrderByDescending(l => l.LockedAt).Select(l => new RetentionLockDto
            {
                Id = l.Id,
                Reason = l.Reason,
                LockedBy = l.LockedBy,
                LockedByName = l.LockedByUser?.DisplayName,
                LockedAt = l.LockedAt,
                ExpiresAt = l.ExpiresAt,
                Active = l.Active && (l.ExpiresAt == null || l.ExpiresAt > DateTime.UtcNow)
            }).ToList()
        };
    }

    /// <summary>备份文件明细（设计书 18.3）</summary>
    public async Task<List<BackupFileDto>> GetFilesAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var exists = await _db.BackupSets.AsNoTracking().AnyAsync(s => s.Id == backupSetId, ct);
        if (!exists)
            throw new NotFoundException("备份版本", backupSetId);

        var files = await _db.BackupFiles.AsNoTracking()
            .Where(f => f.BackupSetId == backupSetId)
            .OrderBy(f => f.RelativePath)
            .ToListAsync(ct);

        return files.Select(MapFile).ToList();
    }

    /// <summary>锁定备份（设计书 18.4，禁止保留清理）</summary>
    public async Task LockAsync(Guid backupSetId, LockBackupRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ValidationFailedException("锁定原因必填");
        if (request.ExpiresAt is not null && request.ExpiresAt <= DateTime.UtcNow)
            throw new ValidationFailedException("锁到期时间必须晚于当前时间");

        var set = await _db.BackupSets.FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (set.Status is BackupSetStatus.Deleted)
            throw new BusinessException("CONFLICT", "备份版本已删除，不能锁定", 409);

        if (set.Locked)
            throw new BusinessException("CONFLICT", "备份版本已处于锁定状态", 409);

        var userId = _context.UserId
            ?? throw new BusinessException("UNAUTHORIZED", "锁定操作需要管理员身份", 401);

        set.Locked = true;
        _db.RetentionLocks.Add(new RetentionLock
        {
            Id = Guid.NewGuid(),
            BackupSetId = set.Id,
            Reason = request.Reason.Trim(),
            LockedBy = userId,
            LockedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt,
            Active = true
        });

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("backup.lock", AuditResult.Success, "backup_set", set.Id,
            afterData: JsonSerializer.Serialize(new { request.Reason, request.ExpiresAt }), ct: ct);
    }

    /// <summary>解锁备份（设计书 18.5）</summary>
    public async Task UnlockAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var set = await _db.BackupSets.FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (!set.Locked)
            throw new BusinessException("CONFLICT", "备份版本未锁定", 409);

        set.Locked = false;

        await _db.RetentionLocks
            .Where(l => l.BackupSetId == backupSetId && l.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Active, false), ct);

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("backup.unlock", AuditResult.Success, "backup_set", set.Id, ct: ct);
    }

    /// <summary>
    /// 隔离（D1）：人工怀疑该备份有问题时的动作——校验通过但怀疑有问题，
    /// 标记后不参与保留计算（RetentionCleanupWorker 只处理 Available 状态）、
    /// 不能用于恢复（RestoreService 只允许对 Available 状态发起恢复），但也不删除。
    /// </summary>
    public async Task QuarantineAsync(Guid backupSetId, QuarantineBackupRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ValidationFailedException("隔离原因必填");

        var set = await _db.BackupSets.FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (set.Status is not (BackupSetStatus.Available or BackupSetStatus.VerificationFailed))
            throw new BusinessException("CONFLICT",
                $"备份版本当前状态为 {EnumMapping.ToSnakeCase(set.Status)}，只有可用或校验失败的版本可以隔离", 409);

        var previousStatus = set.Status;
        set.Status = BackupSetStatus.Quarantined;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("backup.quarantine", AuditResult.Success, "backup_set", set.Id,
            beforeData: JsonSerializer.Serialize(new { Status = EnumMapping.ToSnakeCase(previousStatus) }),
            afterData: JsonSerializer.Serialize(new { request.Reason }), ct: ct);

        _logger.LogInformation("备份 {BackupSetId}({Code}) 已隔离：{Reason}", set.Id, set.BackupSetCode, request.Reason);
    }

    /// <summary>解除隔离（D1），恢复为可用状态</summary>
    public async Task UnquarantineAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var set = await _db.BackupSets.FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (set.Status is not BackupSetStatus.Quarantined)
            throw new BusinessException("CONFLICT", "备份版本未处于隔离状态", 409);

        set.Status = BackupSetStatus.Available;

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("backup.unquarantine", AuditResult.Success, "backup_set", set.Id, ct: ct);

        _logger.LogInformation("备份 {BackupSetId}({Code}) 已解除隔离", set.Id, set.BackupSetCode);
    }

    // ---------- 删除 / 回收站（待办方案 D） ----------

    /// <summary>回收站默认保留天数：备份集所属任务没绑保留策略时用它</summary>
    private const int DefaultRecycleBinDays = 7;

    public async Task RecycleAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var set = await _db.BackupSets
            .Include(s => s.RetentionLocks)
            .Include(s => s.Task).ThenInclude(t => t.RetentionPolicy)
            .FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (set.Status is BackupSetStatus.RecycleBin or BackupSetStatus.Deleted)
            throw new BusinessException("CONFLICT", "该备份集已经在回收站里或已删除", 409);

        await EnsureRemovableAsync(set, ct);

        var now = DateTime.UtcNow;
        var days = set.Task?.RetentionPolicy?.RecycleBinDays ?? DefaultRecycleBinDays;
        var previousStatus = set.Status;

        set.Status = BackupSetStatus.RecycleBin;
        set.RetentionUntil = now.AddDays(days);
        await _db.SaveChangesAsync(ct);

        await InvalidateScanBaselineAsync(set, ct);

        await _audit.RecordAsync("backup.recycle", AuditResult.Success, "backup_set", set.Id,
            beforeData: JsonSerializer.Serialize(new { Status = EnumMapping.ToSnakeCase(previousStatus) }),
            afterData: JsonSerializer.Serialize(new { set.BackupSetCode, retentionUntil = set.RetentionUntil }), ct: ct);

        _logger.LogInformation("备份 {Code} 已移入回收站，{Days} 天内可还原（保留至 {Until:yyyy-MM-dd}）",
            set.BackupSetCode, days, set.RetentionUntil);
    }

    public async Task RestoreFromRecycleBinAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var set = await _db.BackupSets.FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (set.Status != BackupSetStatus.RecycleBin)
            throw new BusinessException("CONFLICT", "只有回收站里的备份集才能还原", 409);

        // 删掉之后允许重新备份（V033），于是同一个候选可能已经有一份新的活着的版本了。
        // 不挡住的话，还原会撞上 uq_backup_sets_candidate_live，
        // 使用者拿到的是一句数据库唯一约束错误，看不出发生了什么。
        var hasLive = await _db.BackupSets.AnyAsync(
            b => b.SourceCandidateId == set.SourceCandidateId
                && b.Id != set.Id
                && BackupSetStatuses.Live.Contains(b.Status), ct);
        if (hasLive)
            throw new BusinessException("CONFLICT",
                "这份备份删除之后已经重新备份过了，还原会和新版本冲突。"
                + "需要旧版本的话请先处理掉新的那一份。", 409);

        set.Status = BackupSetStatus.Available;
        set.RetentionUntil = null;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("backup.restore_from_recycle_bin", AuditResult.Success, "backup_set", set.Id,
            afterData: JsonSerializer.Serialize(new { set.BackupSetCode }), ct: ct);

        _logger.LogInformation("备份 {Code} 已从回收站还原", set.BackupSetCode);
    }

    public async Task PurgeAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var set = await _db.BackupSets
            .Include(s => s.RetentionLocks)
            .FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        // 「立即彻底删除」只对回收站开放：要腾空间的人先把它删进回收站，再决定要不要真删。
        // 这一步不可撤销，不该有从「可用」一键直达的路径。
        if (set.Status != BackupSetStatus.RecycleBin)
            throw new BusinessException("CONFLICT", "只有回收站里的备份集才能彻底删除", 409);

        await EnsureRemovableAsync(set, ct);

        var repositoryRoot = await _storage.GetRepositoryRootAsync(ct);
        if (!RepositoryDirectory.DeleteUnderRoot(set.RepositoryPath, repositoryRoot))
            _logger.LogWarning("备份集 {Code} 仓库目录不存在，只做逻辑删除：{Path}", set.BackupSetCode, set.RepositoryPath);

        set.Status = BackupSetStatus.Deleted;
        await _db.SaveChangesAsync(ct);

        await InvalidateScanBaselineAsync(set, ct);

        await _audit.RecordAsync("backup.purge", AuditResult.Success, "backup_set", set.Id,
            afterData: JsonSerializer.Serialize(new { set.BackupSetCode, set.RepositoryPath }), ct: ct);

        _logger.LogInformation("备份 {Code} 已彻底删除：{Path}", set.BackupSetCode, set.RepositoryPath);
    }

    // ---------- 批量回收站操作 ----------

    /// <summary>
    /// 单批上限。彻底删除是同步删目录的，一次几百个目录在慢盘或网络存储上
    /// 足以把请求拖到超时；超过就让人分批，而不是给一个偶尔会半路断掉的按钮。
    /// </summary>
    public const int MaxBatchSize = 100;

    public Task<BackupSetBatchResult> RecycleBatchAsync(
        BackupSetBatchRequest request, CancellationToken ct = default)
        => RunBatchAsync(request, RecycleAsync, "batch.recycle", ct);

    public Task<BackupSetBatchResult> RestoreFromRecycleBinBatchAsync(
        BackupSetBatchRequest request, CancellationToken ct = default)
        => RunBatchAsync(request, RestoreFromRecycleBinAsync, "batch.restore_from_recycle_bin", ct);

    public Task<BackupSetBatchResult> PurgeBatchAsync(
        BackupSetBatchRequest request, CancellationToken ct = default)
        => RunBatchAsync(request, PurgeAsync, "batch.purge", ct);

    /// <summary>
    /// 逐条执行，逐条记账。
    ///
    /// 刻意不包在一个事务里：选中的 36 份里有一份被锁定、或者删掉之后已经重新备份过，
    /// 那一条本来就该被拒（守卫与单条操作完全一致），但它不该让另外 35 份也回不来。
    /// 所以每条各自成败，最后把「成功几条、哪几条为什么没做成」一起交回去。
    /// </summary>
    private async Task<BackupSetBatchResult> RunBatchAsync(
        BackupSetBatchRequest request,
        Func<Guid, CancellationToken, Task> action,
        string operation,
        CancellationToken ct)
    {
        var ids = (request?.BackupSetIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count == 0)
            throw new BusinessException("INVALID_REQUEST", "没有选中任何备份集", 400);
        if (ids.Count > MaxBatchSize)
            throw new BusinessException("INVALID_REQUEST",
                $"一次最多处理 {MaxBatchSize} 份备份集，当前选中 {ids.Count} 份，请分批操作", 400);

        // 备份集编号先查出来：失败明细里只给 id，界面上报出来的那几条人是对不上号的。
        var codes = await _db.BackupSets.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.BackupSetCode })
            .ToDictionaryAsync(s => s.Id, s => s.BackupSetCode, ct);

        var result = new BackupSetBatchResult();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            codes.TryGetValue(id, out var code);
            try
            {
                await action(id, ct);
                result.SuccessCount++;
            }
            catch (BusinessException ex)
            {
                result.Failed.Add(new BackupSetBatchFailure
                {
                    BackupSetId = id, BackupSetCode = code, ErrorCode = ex.ErrorCode, Message = ex.Message
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 删目录撞上文件被占用这类意外，只该让这一条失败。
                // 让它冒出去的话，前面已经提交的那些既没回滚也没人报告，
                // 使用者拿到的是一个 500 和一份不知道删到哪一条的列表。
                _logger.LogError(ex, "批量操作 {Operation} 处理备份集 {BackupSetId}({Code}) 失败", operation, id, code);
                result.Failed.Add(new BackupSetBatchFailure
                {
                    BackupSetId = id, BackupSetCode = code, ErrorCode = "ERROR", Message = ex.Message
                });
            }
        }

        _logger.LogInformation("批量操作 {Operation}：共 {Total} 份，成功 {Ok} 份，失败 {Fail} 份",
            operation, ids.Count, result.SuccessCount, result.Failed.Count);

        return result;
    }

    /// <summary>
    /// 备份集删掉之后，让 Agent 那边的两段式扫描基线失效。
    ///
    /// 不做这一步的话，放开六处应用层闸和数据库唯一索引都没有用：源文件没变
    /// → Agent 的快速指纹命中上一次的候选 → 直接回 no_new_backup，
    /// 人在界面上看到的是「没有新备份」，而他刚刚删掉的正是那一份。
    ///
    /// ConfigVersion + 1 是必须的：Agent 只在配置版本变高时才重新拉配置，
    /// 只清指纹不推版本号，新的基线要等到下一次别的配置变更才到得了客户端。
    /// </summary>
    private async Task InvalidateScanBaselineAsync(BackupSet set, CancellationToken ct)
    {
        await _db.CandidateBackupSets
            .Where(c => c.Id == set.SourceCandidateId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.QuickFingerprint, (string?)null), ct);

        await _db.BackupTasks
            .Where(t => t.Id == set.TaskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConfigVersion, t => t.ConfigVersion + 1), ct);
    }

    /// <summary>
    /// 能不能删。三条守卫与 RetentionCleanupWorker 判的是同一件事——
    /// 自动清理不敢删的东西，人在界面上点一下也不该能删掉。
    /// </summary>
    private async Task EnsureRemovableAsync(BackupSet set, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if (set.Locked)
            throw new BusinessException("CONFLICT", "该备份集已被锁定，先解锁再删除", 409);

        if (set.RetentionLocks.Any(l => l.Active && (l.ExpiresAt == null || l.ExpiresAt > now)))
            throw new BusinessException("CONFLICT", "该备份集上有生效中的保留锁，先解除保留锁再删除", 409);

        var restoring = await _db.RestoreRequests.AnyAsync(r => r.BackupSetId == set.Id
            && (r.Status == RestoreRequestStatus.Verifying
                || r.Status == RestoreRequestStatus.Downloading
                || (r.Status == RestoreRequestStatus.Ready && r.DownloadExpiresAt != null)), ct);
        if (restoring)
            throw new BusinessException("CONFLICT", "该备份集正在被恢复下载，等这次恢复结束再删除", 409);
    }

    /// <summary>重新校验（设计书 18.6，异步：入后台校验队列）</summary>
    public async Task<VerifyBackupResponse> VerifyAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var set = await _db.BackupSets.FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (set.VerifyingSince is not null)
            throw new BusinessException("CONFLICT", "备份版本正在校验中", 409);
        if (set.Status is BackupSetStatus.RecycleBin or BackupSetStatus.Deleted)
            throw new BusinessException("CONFLICT", "备份版本已进入回收站/已删除，不能校验", 409);

        // 隔离是「我怀疑这一份有问题，先别用也别删」的人工判断。哈希对得上并不代表
        // 当初隔离的理由消失了——校验只能证明「文件和存进来时一样」，
        // 而隔离的理由通常是「存进来的那一份本身就可疑」。让重新校验把它变回可用，
        // 等于给了一个绕过人工判断的后门，而且没有任何提示。
        if (set.Status is BackupSetStatus.Quarantined)
            throw new BusinessException("CONFLICT",
                "这一份已被人工隔离。重新校验只能证明文件没被改动，不能解除隔离；"
                + "确认没问题请先在详情里解除隔离。", 409);

        // 不动 Status：原状态要留着，校验完按原状态之上的结论回写。
        set.VerifyingSince = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _workChannel.Writer.WriteAsync(new WorkItem(WorkKind.ReverifyBackupSet, set.Id), ct);

        await _audit.RecordAsync("backup.reverify", AuditResult.Success, "backup_set", set.Id, ct: ct);
        _logger.LogInformation("备份 {BackupSetId}({Code}) 已加入重校验队列", set.Id, set.BackupSetCode);

        return new VerifyBackupResponse { OperationId = set.Id };
    }

    private static BackupFileDto MapFile(Core.Entities.Backup.BackupFile f) => new()
    {
        Id = f.Id,
        RelativePath = f.RelativePath,
        FileName = f.FileName,
        SizeBytes = f.SizeBytes,
        LastModifiedAt = f.LastModifiedAt,
        Sha256 = f.Sha256,
        VerificationStatus = EnumMapping.ToSnakeCase(f.VerificationStatus)
    };
}
