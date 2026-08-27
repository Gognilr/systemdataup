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
    Task<BackupSetDetailDto> GetDetailAsync(Guid backupSetId, CancellationToken ct = default);
    Task<List<BackupFileDto>> GetFilesAsync(Guid backupSetId, CancellationToken ct = default);
    Task LockAsync(Guid backupSetId, LockBackupRequest request, CancellationToken ct = default);
    Task UnlockAsync(Guid backupSetId, CancellationToken ct = default);
    Task<VerifyBackupResponse> VerifyAsync(Guid backupSetId, CancellationToken ct = default);

    /// <summary>隔离（D1）：人工怀疑该备份有问题时标记，不参与保留计算、不能用于恢复，但也不删除</summary>
    Task QuarantineAsync(Guid backupSetId, QuarantineBackupRequest request, CancellationToken ct = default);

    /// <summary>解除隔离，恢复为可用状态</summary>
    Task UnquarantineAsync(Guid backupSetId, CancellationToken ct = default);
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
    private readonly ILogger<BackupSetService> _logger;

    public BackupSetService(
        AppDbContext db,
        [FromKeyedServices(QueueKeys.Verify)] Channel<WorkItem> workChannel,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<BackupSetService> logger)
    {
        _db = db;
        _workChannel = workChannel;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task<PagedResult<BackupSetListItemDto>> GetListAsync(BackupQuery query, CancellationToken ct = default)
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

        if (query.From is not null)
            sets = sets.Where(s => s.UploadedAt >= query.From);
        if (query.To is not null)
            sets = sets.Where(s => s.UploadedAt <= query.To);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
            sets = sets.Where(s => EF.Functions.ILike(s.BackupSetCode, $"%{query.Keyword.Trim()}%"));

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
                s.BackupBusinessTime,
                s.UploadedAt,
                s.TotalFiles,
                s.TotalBytes,
                s.Locked,
                s.RetentionUntil
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
            BackupBusinessTime = s.BackupBusinessTime,
            UploadedAt = s.UploadedAt,
            TotalFiles = s.TotalFiles,
            TotalBytes = s.TotalBytes,
            Locked = s.Locked,
            RetentionUntil = s.RetentionUntil
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
            BackupBusinessTime = set.BackupBusinessTime,
            UploadedAt = set.UploadedAt,
            TotalFiles = set.TotalFiles,
            TotalBytes = set.TotalBytes,
            Locked = set.Locked,
            RetentionUntil = set.RetentionUntil,

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

    /// <summary>重新校验（设计书 18.6，异步：入后台校验队列）</summary>
    public async Task<VerifyBackupResponse> VerifyAsync(Guid backupSetId, CancellationToken ct = default)
    {
        var set = await _db.BackupSets.FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (set.Status is BackupSetStatus.Verifying)
            throw new BusinessException("CONFLICT", "备份版本正在校验中", 409);
        if (set.Status is BackupSetStatus.RecycleBin or BackupSetStatus.Deleted)
            throw new BusinessException("CONFLICT", "备份版本已进入回收站/已删除，不能校验", 409);

        set.Status = BackupSetStatus.Verifying;
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
