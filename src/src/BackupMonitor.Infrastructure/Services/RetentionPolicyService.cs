using System.Text.Json;
using BackupMonitor.Core.Entities.Retention;
using BackupMonitor.Core.Entities.System;
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

    /// <summary>把该策略设为「新建任务默认」（写 system_settings.default_retention_policy_id）</summary>
    Task<RetentionPolicyDto> SetDefaultAsync(Guid id, CancellationToken ct = default);
}

/// <summary>保留策略实现（CRUD；保留清理由定时任务负责，见设计书 25）</summary>
public class RetentionPolicyService : IRetentionPolicyService
{
    /// <summary>新建任务默认绑定哪份策略（system_settings 键，V027）。空串表示不自动绑定。</summary>
    public const string DefaultPolicySettingKey = "default_retention_policy_id";

    private readonly AppDbContext _db;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly SystemSettingsProvider _settings;
    private readonly ILogger<RetentionPolicyService> _logger;

    public RetentionPolicyService(
        AppDbContext db,
        ICurrentContext context,
        IAuditRecorder audit,
        SystemSettingsProvider settings,
        ILogger<RetentionPolicyService> logger)
    {
        _db = db;
        _context = context;
        _audit = audit;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 读取默认策略 ID。配置缺失、值为空串或不是一个 GUID 都返回 null（= 不自动绑定），
    /// 不抛异常——这条配置坏掉不该让「建任务」整个失败。
    /// </summary>
    public static async Task<Guid?> GetDefaultPolicyIdAsync(SystemSettingsProvider settings, CancellationToken ct)
    {
        var raw = await settings.GetStringAsync(DefaultPolicySettingKey, ct);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// 把某份策略设为「新建任务默认」。
    ///
    /// 刻意没有配套的「清除默认」：没有默认策略之后，不选策略的任务会退回「不绑策略」，
    /// 而这个状态在清理器里等同于永不清理——正是 V027 花力气堵上的坑。
    /// 真要回到旧行为改一条 SQL 就够了，不值得在界面上留一个自毁开关。
    /// </summary>
    public async Task<RetentionPolicyDto> SetDefaultAsync(Guid id, CancellationToken ct = default)
    {
        var policy = await _db.RetentionPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("保留策略", id);

        var beforeId = await GetDefaultPolicyIdAsync(_settings, ct);
        var boundTaskCount = await _db.BackupTasks.CountAsync(t => t.RetentionPolicyId == id, ct);

        // 已经是默认就直接返回：反复点同一行不该在审计里刷出一串「没有变化的变更」
        if (beforeId == id)
            return Map(policy, boundTaskCount, id);

        // 改前的策略名必须在这里取。审计里只留 ID 的话，事后翻记录还得反查这个 ID 是谁，
        // 而它很可能已经被删掉了——改配默认策略正是为了能删掉旧的那份。
        var beforeName = beforeId is null
            ? null
            : await _db.RetentionPolicies.AsNoTracking()
                .Where(p => p.Id == beforeId.Value)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct);

        var json = JsonSerializer.Serialize(id.ToString());
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == DefaultPolicySettingKey, ct);
        if (row is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                SettingKey = DefaultPolicySettingKey,
                SettingValue = json,
                Encrypted = false,
                UpdatedBy = _context.UserId
            });
        }
        else
        {
            row.SettingValue = json;
            row.UpdatedBy = _context.UserId;
        }

        await _db.SaveChangesAsync(ct);

        // SystemSettingsProvider 是单例、缓存 60 秒：不作废的话，刚点完「设为默认」转头建任务
        // 绑上的还是旧策略，界面上表现为「设置没保存」。
        _settings.Invalidate();

        // 保留策略决定数据什么时候被删，改配默认策略必须留痕（改前改后的 ID 与名称都记）
        await _audit.RecordAsync("retention.default_changed", AuditResult.Success, "retention_policy", policy.Id,
            beforeData: JsonSerializer.Serialize(new { policyId = beforeId, policyName = beforeName }),
            afterData: JsonSerializer.Serialize(new { policyId = policy.Id, policyName = policy.Name }), ct: ct);

        _logger.LogInformation("新建任务默认保留策略已改为 {PolicyId}({Name})，原为 {BeforePolicyId}({BeforeName})",
            policy.Id, policy.Name, beforeId, beforeName);

        return Map(policy, boundTaskCount, id);
    }

    public async Task<List<RetentionPolicyDto>> GetListAsync(CancellationToken ct = default)
    {
        var defaultId = await GetDefaultPolicyIdAsync(_settings, ct);

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
            BoundTaskCount = p.BoundTaskCount,
            IsDefault = p.Id == defaultId
        }).ToList();
    }

    public async Task<RetentionPolicyDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var policy = await _db.RetentionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("保留策略", id);

        var boundTaskCount = await _db.BackupTasks.CountAsync(t => t.RetentionPolicyId == id, ct);

        return Map(policy, boundTaskCount, await GetDefaultPolicyIdAsync(_settings, ct));
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
        return Map(policy, 0, await GetDefaultPolicyIdAsync(_settings, ct));
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
        return Map(policy, boundTaskCount, await GetDefaultPolicyIdAsync(_settings, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var policy = await _db.RetentionPolicies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("保留策略", id);

        var boundTaskCount = await _db.BackupTasks.CountAsync(t => t.RetentionPolicyId == id, ct);
        if (boundTaskCount > 0)
            throw new BusinessException("CONFLICT", $"仍有 {boundTaskCount} 个备份任务使用该策略，不能删除", 409);

        // 删掉默认策略会让之后新建的任务重新回到「不绑定 = 永不清理」，正是 V027 要消灭的状态
        if (await GetDefaultPolicyIdAsync(_settings, ct) == id)
            throw new BusinessException("CONFLICT", "该策略是新建任务的默认策略，请先改配默认策略再删除", 409);

        _db.RetentionPolicies.Remove(policy);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("retention_policy.delete", AuditResult.Success, "retention_policy", policy.Id,
            beforeData: JsonSerializer.Serialize(new { policy.Name }), ct: ct);

        _logger.LogInformation("保留策略 {PolicyId}({Name}) 已删除", policy.Id, policy.Name);
    }

    private static RetentionPolicyDto Map(RetentionPolicy p, int boundTaskCount, Guid? defaultPolicyId) => new()
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
        BoundTaskCount = boundTaskCount,
        IsDefault = p.Id == defaultPolicyId
    };
}
