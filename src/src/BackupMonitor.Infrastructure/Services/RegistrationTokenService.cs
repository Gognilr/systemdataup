using System.Text.Json;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

public interface IRegistrationTokenService
{
    Task<IReadOnlyList<RegistrationTokenDto>> ListAsync(RegistrationTokenQuery query, CancellationToken ct = default);
    Task<CreateRegistrationTokenResponse> CreateAsync(CreateRegistrationTokenRequest request, CancellationToken ct = default);
    Task RevokeAsync(Guid tokenId, RevokeRegistrationTokenRequest request, CancellationToken ct = default);
}

public sealed class RegistrationTokenService : IRegistrationTokenService
{
    private readonly AppDbContext _db;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;

    public RegistrationTokenService(AppDbContext db, ICurrentContext context, IAuditRecorder audit)
    {
        _db = db;
        _context = context;
        _audit = audit;
    }

    public async Task<IReadOnlyList<RegistrationTokenDto>> ListAsync(RegistrationTokenQuery query, CancellationToken ct = default)
    {
        var tokens = await _db.RegistrationTokens
            .AsNoTracking()
            .Include(t => t.ClientGroup)
            .Where(t => query.IncludeInactive || t.Status == RegistrationTokenStatus.Active)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return tokens.Select(Map).ToList();
    }

    public async Task<CreateRegistrationTokenResponse> CreateAsync(CreateRegistrationTokenRequest request, CancellationToken ct = default)
    {
        if (request.ExpiresAt is not null && request.ExpiresAt <= DateTime.UtcNow)
            throw new ValidationFailedException("过期时间必须晚于当前时间");

        if (request.ClientGroupId is not null && !await _db.ClientGroups.AnyAsync(g => g.Id == request.ClientGroupId, ct))
            throw new NotFoundException("客户端分组", request.ClientGroupId);

        var token = TokenHasher.GenerateToken(32);
        var entity = new RegistrationToken
        {
            Id = Guid.NewGuid(),
            TokenHash = TokenHasher.Sha256Hex(token),
            Name = request.Name.Trim(),
            ClientGroupId = request.ClientGroupId,
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            MaxUses = request.MaxUses,
            UsedCount = 0,
            Status = RegistrationTokenStatus.Active,
            CreatedBy = _context.UserId,
            CreatedAt = DateTime.UtcNow
        };

        _db.RegistrationTokens.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("registration_token.create", AuditResult.Success, "registration_token", entity.Id,
            afterData: JsonSerializer.Serialize(new { entity.Name, entity.ClientGroupId, entity.ExpiresAt, entity.MaxUses }), ct: ct);

        entity.ClientGroup = await _db.ClientGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == entity.ClientGroupId, ct);
        return new CreateRegistrationTokenResponse
        {
            Token = token,
            RegistrationToken = Map(entity)
        };
    }

    public async Task RevokeAsync(Guid tokenId, RevokeRegistrationTokenRequest request, CancellationToken ct = default)
    {
        var token = await _db.RegistrationTokens.FirstOrDefaultAsync(t => t.Id == tokenId, ct)
            ?? throw new NotFoundException("注册令牌", tokenId);
        if (token.Status == RegistrationTokenStatus.Revoked)
            return;

        token.Status = RegistrationTokenStatus.Revoked;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("registration_token.revoke", AuditResult.Success, "registration_token", token.Id,
            afterData: JsonSerializer.Serialize(new { reason = request.Reason }), ct: ct);
    }

    private static RegistrationTokenDto Map(RegistrationToken token) => new()
    {
        Id = token.Id,
        Name = token.Name,
        ClientGroupId = token.ClientGroupId,
        ClientGroupName = token.ClientGroup?.Name,
        ExpiresAt = token.ExpiresAt,
        MaxUses = token.MaxUses,
        UsedCount = token.UsedCount,
        Status = EnumMapping.ToSnakeCase(token.Status),
        CreatedAt = token.CreatedAt
    };
}
