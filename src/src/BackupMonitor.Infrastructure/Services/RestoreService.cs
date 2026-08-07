using System.Text.Json;
using System.Threading.Channels;
using BackupMonitor.Core.Entities.Restore;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>恢复下载服务（设计书 19 恢复下载接口；令牌签发与下载计量为补充设计）</summary>
public interface IRestoreService
{
    /// <summary>创建恢复请求（19.1），入库完整性校验异步执行</summary>
    Task<CreateRestoreResponseDto> CreateAsync(CreateRestoreRequestDto request, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>查询恢复请求（19.2）</summary>
    Task<RestoreRequestDto> GetAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>恢复请求列表（补充设计）</summary>
    Task<PagedResult<RestoreRequestDto>> GetListAsync(RestoreQuery query, CancellationToken ct = default);

    /// <summary>签发/换发下载令牌（补充设计：明文只在此响应中出现一次）</summary>
    Task<RestoreDownloadTokenDto> IssueDownloadTokenAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>凭令牌开始下载（19.3）：校验令牌、记录 IP、返回仓库文件清单</summary>
    Task<RestoreDownloadContext> BeginDownloadAsync(string token, string? clientIp, CancellationToken ct = default);

    /// <summary>下载结束后记账（字节数与完成状态）</summary>
    Task FinishDownloadAsync(Guid requestId, long bytesTransferred, bool completed, CancellationToken ct = default);
}

/// <summary>恢复下载可下载的文件条目</summary>
public record RestoreDownloadFileInfo(string RelativePath, string RepositoryRelativePath, string FileName, long SizeBytes);

/// <summary>恢复下载上下文（令牌校验通过后交给下载控制器流式输出）</summary>
public record RestoreDownloadContext(
    Guid RequestId,
    Guid BackupSetId,
    string BackupSetCode,
    string RepositoryPath,
    IReadOnlyList<RestoreDownloadFileInfo> Files);

/// <summary>恢复下载实现</summary>
public class RestoreService : IRestoreService
{
    /// <summary>幂等作用域（设计书 8.5；DB 级持久化，重启/多实例不丢）</summary>
    private const string IdempotencyScope = "restore.create";

    /// <summary>幂等有效期：同键 10 分钟内返回原结果</summary>
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _db;
    private readonly Channel<WorkItem> _workChannel;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly SystemSettingsProvider _settings;
    private readonly IIdempotencyStore _idempotency;
    private readonly ILogger<RestoreService> _logger;

    public RestoreService(
        AppDbContext db,
        Channel<WorkItem> workChannel,
        ICurrentContext context,
        IAuditRecorder audit,
        SystemSettingsProvider settings,
        IIdempotencyStore idempotency,
        ILogger<RestoreService> logger)
    {
        _db = db;
        _workChannel = workChannel;
        _context = context;
        _audit = audit;
        _settings = settings;
        _idempotency = idempotency;
        _logger = logger;
    }

    public async Task<CreateRestoreResponseDto> CreateAsync(CreateRestoreRequestDto request, string? idempotencyKey, CancellationToken ct = default)
    {
        var backupSetId = request.BackupSetId
            ?? throw new ValidationFailedException("backupSetId 必填");
        if (string.IsNullOrWhiteSpace(request.Purpose))
            throw new ValidationFailedException("purpose 必填");
        if (request.Selection is not null
            && !string.Equals(request.Selection.Type, "full", StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("INVALID_REQUEST", "V1 仅支持 full（整个备份集）恢复", 400);

        // 幂等快路径：同一 Idempotency-Key 10 分钟内返回原结果（设计书 8.5；DB 级持久化，重启/多实例不丢）
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existingId = await _idempotency.FindAsync(IdempotencyScope, idempotencyKey, ct);
            if (existingId is not null)
                return ReplayedResponse(existingId.Value);
        }

        var set = await _db.BackupSets.FirstOrDefaultAsync(s => s.Id == backupSetId, ct)
            ?? throw new NotFoundException("备份版本", backupSetId);

        if (set.Status is not BackupSetStatus.Available)
            throw new BusinessException("CONFLICT",
                $"备份版本状态为 {EnumMapping.ToSnakeCase(set.Status)}，不能创建恢复请求", 409);

        var userId = _context.UserId
            ?? throw new BusinessException("UNAUTHORIZED", "创建恢复请求需要管理员身份", 401);

        var entity = new RestoreRequest
        {
            Id = Guid.NewGuid(),
            BackupSetId = set.Id,
            RequestedBy = userId,
            Purpose = request.Purpose.Trim(),
            Status = RestoreRequestStatus.Verifying,
            RequestedAt = DateTime.UtcNow,
            DownloadedBytes = 0
        };
        _db.RestoreRequests.Add(entity);

        // 幂等键与业务实体同事务登记：一起提交或一起回滚
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            _idempotency.Track(IdempotencyScope, idempotencyKey, entity.Id, IdempotencyTtl);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (_idempotency.IsDuplicateKeyException(ex))
        {
            // 并发重复请求：先到的请求已提交同键，本事务整体回滚（恢复请求也未落库），返回首次结果
            var winnerId = string.IsNullOrWhiteSpace(idempotencyKey)
                ? null
                : await _idempotency.FindAsync(IdempotencyScope, idempotencyKey, ct);
            if (winnerId is null)
                throw new BusinessException("CONFLICT", "幂等键已被其他请求占用", 409);
            return ReplayedResponse(winnerId.Value);
        }

        // 下载前完整性校验（设计书 19.3）异步入队
        await _workChannel.Writer.WriteAsync(new WorkItem(WorkKind.VerifyRestoreRequest, entity.Id), ct);

        await _audit.RecordAsync("restore.create", AuditResult.Success, "restore_request", entity.Id,
            afterData: JsonSerializer.Serialize(new { backupSetId = set.Id, purpose = entity.Purpose }), ct: ct);

        _logger.LogInformation("恢复请求 {RequestId} 已创建，备份 {Code} 进入下载前校验", entity.Id, set.BackupSetCode);

        return new CreateRestoreResponseDto
        {
            RestoreRequestId = entity.Id,
            Status = EnumMapping.ToSnakeCase(entity.Status)
        };
    }

    public async Task<RestoreRequestDto> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        var entity = await _db.RestoreRequests.AsNoTracking()
            .Include(r => r.BackupSet).ThenInclude(s => s.Client)
            .Include(r => r.RequestedByUser)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new NotFoundException("恢复请求", requestId);

        return Map(entity);
    }

    public async Task<PagedResult<RestoreRequestDto>> GetListAsync(RestoreQuery query, CancellationToken ct = default)
    {
        var q = _db.RestoreRequests.AsNoTracking().AsQueryable();

        if (query.BackupSetId is not null)
            q = q.Where(r => r.BackupSetId == query.BackupSetId);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!EnumMapping.TryParseSnakeCase<RestoreRequestStatus>(query.Status, out var status))
                throw new BusinessException("INVALID_REQUEST", $"无效的恢复请求状态：{query.Status}", 400);
            q = q.Where(r => r.Status == status);
        }

        var totalCount = await q.LongCountAsync(ct);

        var rows = await q
            .OrderByDescending(r => r.RequestedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(r => new
            {
                r.Id,
                r.BackupSetId,
                BackupSetCode = r.BackupSet.BackupSetCode,
                ClientHostname = r.BackupSet.Client.Hostname,
                r.Purpose,
                r.Status,
                r.RequestedAt,
                r.VerifiedAt,
                r.DownloadExpiresAt,
                r.DownloadedBytes,
                r.CompletedAt,
                r.ClientIp,
                r.ErrorMessage,
                r.RequestedBy,
                RequestedByName = r.RequestedByUser.DisplayName
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new RestoreRequestDto
        {
            Id = r.Id,
            BackupSetId = r.BackupSetId,
            BackupSetCode = r.BackupSetCode,
            ClientHostname = r.ClientHostname,
            Purpose = r.Purpose,
            Status = EnumMapping.ToSnakeCase(r.Status),
            RequestedAt = r.RequestedAt,
            VerifiedAt = r.VerifiedAt,
            DownloadExpiresAt = r.DownloadExpiresAt,
            DownloadedBytes = r.DownloadedBytes,
            CompletedAt = r.CompletedAt,
            ClientIp = r.ClientIp,
            ErrorMessage = r.ErrorMessage,
            RequestedBy = r.RequestedBy,
            RequestedByName = r.RequestedByName
        }).ToList();

        return PagedResult<RestoreRequestDto>.Create(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<RestoreDownloadTokenDto> IssueDownloadTokenAsync(Guid requestId, CancellationToken ct = default)
    {
        var entity = await _db.RestoreRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new NotFoundException("恢复请求", requestId);

        if (entity.Status is RestoreRequestStatus.Requested or RestoreRequestStatus.Verifying)
            throw new BusinessException("CONFLICT", "恢复请求正在校验中，请稍后再试", 409);
        if (entity.Status is RestoreRequestStatus.Failed or RestoreRequestStatus.Expired)
            throw new BusinessException("CONFLICT",
                $"恢复请求状态为 {EnumMapping.ToSnakeCase(entity.Status)}，不能签发下载令牌", 409);

        var ttlMinutes = await _settings.GetIntAsync("download_token_ttl_minutes", 30, ct);
        var token = TokenHasher.GenerateToken(32);

        // 换发即失效旧令牌（库中只保存哈希）
        entity.DownloadTokenHash = TokenHasher.Sha256Hex(token);
        entity.DownloadExpiresAt = DateTime.UtcNow.AddMinutes(ttlMinutes);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("restore.issue_token", AuditResult.Success, "restore_request", entity.Id, ct: ct);

        return new RestoreDownloadTokenDto
        {
            RestoreRequestId = entity.Id,
            DownloadToken = token,
            DownloadUrl = $"/api/v1/downloads/{token}",
            ExpiresAt = entity.DownloadExpiresAt!.Value
        };
    }

    public async Task<RestoreDownloadContext> BeginDownloadAsync(string token, string? clientIp, CancellationToken ct = default)
    {
        var tokenHash = TokenHasher.Sha256Hex(token);
        var entity = await _db.RestoreRequests
            .FirstOrDefaultAsync(r => r.DownloadTokenHash == tokenHash, ct)
            ?? throw new BusinessException("NOT_FOUND", "下载链接无效", 404);

        if (entity.DownloadExpiresAt is null || entity.DownloadExpiresAt <= DateTime.UtcNow)
            throw new BusinessException("CONFLICT", "下载链接已过期，请重新签发令牌", 409);

        if (entity.Status is RestoreRequestStatus.Requested or RestoreRequestStatus.Verifying
            or RestoreRequestStatus.Failed or RestoreRequestStatus.Expired)
            throw new BusinessException("CONFLICT",
                $"恢复请求状态为 {EnumMapping.ToSnakeCase(entity.Status)}，不能下载", 409);

        var set = await _db.BackupSets.AsNoTracking()
            .Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == entity.BackupSetId, ct);

        if (set is null || string.IsNullOrWhiteSpace(set.RepositoryPath))
            throw new BusinessException("STORAGE_UNAVAILABLE", "备份仓库路径缺失，无法下载", 503);

        if (set.Status is not BackupSetStatus.Available)
            throw new BusinessException("CONFLICT", "备份版本当前状态不可用，不能下载", 409);

        entity.Status = RestoreRequestStatus.Downloading;
        entity.ClientIp = clientIp;
        await _db.SaveChangesAsync(ct);

        return new RestoreDownloadContext(
            entity.Id,
            set.Id,
            set.BackupSetCode,
            set.RepositoryPath,
            set.Files
                .OrderBy(f => f.RelativePath)
                .Select(f => new RestoreDownloadFileInfo(f.RelativePath, f.RepositoryRelativePath, f.FileName, f.SizeBytes))
                .ToList());
    }

    public async Task FinishDownloadAsync(Guid requestId, long bytesTransferred, bool completed, CancellationToken ct = default)
    {
        var entity = await _db.RestoreRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (entity is null)
            return;

        // 记账守卫：仅 downloading 状态接受记账。过期清扫（RetentionCleanupWorker 阶段零）
        // 或并发请求已将状态推进到终态时，迟到的记账直接跳过，避免污染字节统计或复活终态。
        if (entity.Status is not RestoreRequestStatus.Downloading)
        {
            _logger.LogWarning("恢复请求 {RequestId} 状态为 {Status}，跳过迟到的下载记账（{Bytes} 字节，completed={Completed}）",
                requestId, EnumMapping.ToSnakeCase(entity.Status), bytesTransferred, completed);
            return;
        }

        entity.DownloadedBytes += bytesTransferred;
        if (completed)
        {
            entity.Status = RestoreRequestStatus.Completed;
            entity.CompletedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("restore.download", AuditResult.Success, "restore_request", requestId,
            afterData: JsonSerializer.Serialize(new { bytes = bytesTransferred, completed }), ct: ct);
    }

    private static RestoreRequestDto Map(RestoreRequest r) => new()
    {
        Id = r.Id,
        BackupSetId = r.BackupSetId,
        BackupSetCode = r.BackupSet?.BackupSetCode,
        ClientHostname = r.BackupSet?.Client?.Hostname,
        Purpose = r.Purpose,
        Status = EnumMapping.ToSnakeCase(r.Status),
        RequestedAt = r.RequestedAt,
        VerifiedAt = r.VerifiedAt,
        DownloadExpiresAt = r.DownloadExpiresAt,
        DownloadedBytes = r.DownloadedBytes,
        CompletedAt = r.CompletedAt,
        ClientIp = r.ClientIp,
        ErrorMessage = r.ErrorMessage,
        RequestedBy = r.RequestedBy,
        RequestedByName = r.RequestedByUser?.DisplayName
    };

    /// <summary>重放响应：幂等命中时返回首次创建的结果（创建时刻状态恒为 verifying）</summary>
    private static CreateRestoreResponseDto ReplayedResponse(Guid restoreRequestId) => new()
    {
        RestoreRequestId = restoreRequestId,
        Status = EnumMapping.ToSnakeCase(RestoreRequestStatus.Verifying)
    };
}
