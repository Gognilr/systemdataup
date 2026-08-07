using System.Text.Json;
using BackupMonitor.Core.Entities.Retention;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>保留策略服务（第二批补充设计：设计书仅定义数据表 retention_policies，接口为补充）</summary>
public interface IRetentionPolicyService
{
    Task<List<RetentionPolicyDto>> GetListAsync(CancellationToken ct = default);
    Task<RetentionPolicyDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<RetentionPolicyDto> CreateAsync(RetentionPolicyUpsertDto request, CancellationToken ct = default);
    Task<RetentionPolicyDto> UpdateAsync(Guid id, RetentionPolicyUpsertDto request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>保留策略实现（CRUD；保留清理由定时任务负责，见设计书 25）</summary>
public class RetentionPolicyService : IRetentionPolicyService
{
    private readonly AppDbContext _db;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<RetentionPolicyService> _logger;

    public RetentionPolicyService(
        AppDbContext db,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<RetentionPolicyService> logger)
    {
        _db = db;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task<List<RetentionPolicyDto>> GetListAsync(CancellationToken ct = default)
    {
        var rows = await _db.RetentionPolicies.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.KeepLastCount,
                p.KeepWeeklyCount,
                p.KeepMonthlyCount,
                p.KeepYearlyCount,
                p.MinimumRetentionDays,
                p.RecycleBinDays,
                p.CreatedAt,
                p.UpdatedAt,
                BoundTaskCount = p.BackupTasks.Count
            })
            .ToListAsync(ct);

        return rows.Select(p => new RetentionPolicyDto
        {
            Id = p.Id,
            Name = p.Name,
            KeepLastCount = p.KeepLastCount,
            KeepWeeklyCount = p.KeepWeeklyCount,
            KeepMonthlyCount = p.KeepMonthlyCount,
            KeepYearlyCount = p.KeepYearlyCount,
            MinimumRetentionDays = p.MinimumRetentionDays,
            RecycleBinDays = p.RecycleBinDays,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            BoundTaskCount = p.BoundTaskCount
        }).ToList();
    }

    public async Task<RetentionPolicyDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var policy = await _db.RetentionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("保留策略", id);

        var boundTaskCount = await _db.BackupTasks.CountAsync(t => t.RetentionPolicyId == id, ct);

        return Map(policy, boundTaskCount);
    }

    public async Task<RetentionPolicyDto> CreateAsync(RetentionPolicyUpsertDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationFailedException("name 必填");

        var policy = new RetentionPolicy
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            KeepLastCount = request.KeepLastCount,
            KeepWeeklyCount = request.KeepWeeklyCount,
            KeepMonthlyCount = request.KeepMonthlyCount,
            KeepYearlyCount = request.KeepYearlyCount,
            MinimumRetentionDays = request.MinimumRetentionDays,
            RecycleBinDays = request.RecycleBinDays,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.RetentionPolicies.Add(policy);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("retention_policy.create", AuditResult.Success, "retention_policy", policy.Id,
            afterData: JsonSerializer.Serialize(new { policy.Name }), ct: ct);

        _logger.LogInformation("保留策略 {PolicyId}({Name}) 已创建", policy.Id, policy.Name);
        return Map(policy, 0);
    }

    public async Task<RetentionPolicyDto> UpdateAsync(Guid id, RetentionPolicyUpsertDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationFailedException("name 必填");

        var policy = await _db.RetentionPolicies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("保留策略", id);

        var before = JsonSerializer.Serialize(new { policy.Name, policy.KeepLastCount, policy.MinimumRetentionDays, policy.RecycleBinDays });

        policy.Name = request.Name.Trim();
        policy.KeepLastCount = request.KeepLastCount;
        policy.KeepWeeklyCount = request.KeepWeeklyCount;
        policy.KeepMonthlyCount = request.KeepMonthlyCount;
        policy.KeepYearlyCount = request.KeepYearlyCount;
        policy.MinimumRetentionDays = request.MinimumRetentionDays;
        policy.RecycleBinDays = request.RecycleBinDays;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("retention_policy.update", AuditResult.Success, "retention_policy", policy.Id,
            beforeData: before,
            afterData: JsonSerializer.Serialize(new { policy.Name }), ct: ct);

        var boundTaskCount = await _db.BackupTasks.CountAsync(t => t.RetentionPolicyId == id, ct);
        return Map(policy, boundTaskCount);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var policy = await _db.RetentionPolicies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("保留策略", id);

        var boundTaskCount = await _db.BackupTasks.CountAsync(t => t.RetentionPolicyId == id, ct);
        if (boundTaskCount > 0)
            throw new BusinessException("CONFLICT", $"仍有 {boundTaskCount} 个备份任务使用该策略，不能删除", 409);

        _db.RetentionPolicies.Remove(policy);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("retention_policy.delete", AuditResult.Success, "retention_policy", policy.Id,
            beforeData: JsonSerializer.Serialize(new { policy.Name }), ct: ct);

        _logger.LogInformation("保留策略 {PolicyId}({Name}) 已删除", policy.Id, policy.Name);
    }

    private static RetentionPolicyDto Map(RetentionPolicy p, int boundTaskCount) => new()
    {
        Id = p.Id,
        Name = p.Name,
        KeepLastCount = p.KeepLastCount,
        KeepWeeklyCount = p.KeepWeeklyCount,
        KeepMonthlyCount = p.KeepMonthlyCount,
        KeepYearlyCount = p.KeepYearlyCount,
        MinimumRetentionDays = p.MinimumRetentionDays,
        RecycleBinDays = p.RecycleBinDays,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        BoundTaskCount = boundTaskCount
    };
}
