using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>审计日志查询服务（设计书 21，只读；审计记录只追加）</summary>
public interface IAuditQueryService
{
    Task<PagedResult<AuditLogDto>> GetListAsync(AuditLogQuery query, CancellationToken ct = default);
}

/// <summary>审计日志查询实现</summary>
public class AuditQueryService : IAuditQueryService
{
    private readonly AppDbContext _db;

    public AuditQueryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<AuditLogDto>> GetListAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        var logs = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (query.UserId is not null)
            logs = logs.Where(l => l.UserId == query.UserId);

        if (!string.IsNullOrWhiteSpace(query.Action))
            logs = logs.Where(l => l.Action == query.Action || l.Action.StartsWith(query.Action + "."));

        if (!string.IsNullOrWhiteSpace(query.ResourceType))
            logs = logs.Where(l => l.ResourceType == query.ResourceType);

        if (query.ResourceId is not null)
            logs = logs.Where(l => l.ResourceId == query.ResourceId);

        if (!string.IsNullOrWhiteSpace(query.Result))
        {
            if (!EnumMapping.TryParseSnakeCase<AuditResult>(query.Result, out var result))
                throw new BusinessException("INVALID_REQUEST", $"无效的审计结果：{query.Result}", 400);
            logs = logs.Where(l => l.Result == result);
        }

        if (query.From is not null)
            logs = logs.Where(l => l.OccurredAt >= query.From);
        if (query.To is not null)
            logs = logs.Where(l => l.OccurredAt <= query.To);

        if (!string.IsNullOrWhiteSpace(query.RequestId))
            logs = logs.Where(l => l.RequestId == query.RequestId);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            logs = logs.Where(l =>
                EF.Functions.ILike(l.Action, $"%{keyword}%")
                || (l.UsernameSnapshot != null && EF.Functions.ILike(l.UsernameSnapshot, $"%{keyword}%")));
        }

        var totalCount = await logs.LongCountAsync(ct);

        var rows = await logs
            .OrderByDescending(l => l.OccurredAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        var items = rows.Select(l => new AuditLogDto
        {
            Id = l.Id,
            OccurredAt = l.OccurredAt,
            UserId = l.UserId,
            UsernameSnapshot = l.UsernameSnapshot,
            ClientIp = l.ClientIp,
            Action = l.Action,
            ResourceType = l.ResourceType,
            ResourceId = l.ResourceId,
            RequestId = l.RequestId,
            Result = EnumMapping.ToSnakeCase(l.Result),
            ErrorCode = l.ErrorCode,
            ErrorMessage = l.ErrorMessage,
            BeforeData = l.BeforeData,
            AfterData = l.AfterData
        }).ToList();

        return PagedResult<AuditLogDto>.Create(items, totalCount, query.Page, query.PageSize);
    }
}
