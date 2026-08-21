using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>服务端向客户端托盘投递短消息的队列。</summary>
public interface IAgentNotificationService
{
    Task EnqueueAsync(
        Guid clientId,
        string kind,
        string severity,
        string title,
        string? message,
        string dedupeKey,
        Guid? alertId = null,
        Guid? backupSetId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentNotificationDto>> ClaimForHeartbeatAsync(
        Guid clientId,
        DateTime since,
        DateTime now,
        int limit = 20,
        CancellationToken ct = default);
}

public sealed class AgentNotificationService : IAgentNotificationService
{
    private readonly AppDbContext _db;

    public AgentNotificationService(AppDbContext db)
    {
        _db = db;
    }

    public async Task EnqueueAsync(
        Guid clientId,
        string kind,
        string severity,
        string title,
        string? message,
        string dedupeKey,
        Guid? alertId = null,
        Guid? backupSetId = null,
        CancellationToken ct = default)
    {
        if (clientId == Guid.Empty || string.IsNullOrWhiteSpace(dedupeKey))
            return;

        var exists = await _db.AgentNotifications.AnyAsync(
            n => n.ClientId == clientId && n.DedupeKey == dedupeKey,
            ct);
        if (exists)
            return;

        var now = DateTime.UtcNow;
        _db.AgentNotifications.Add(new AgentNotification
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Kind = Truncate(kind, 32),
            Severity = Truncate(severity, 16),
            Title = Truncate(title, 255),
            Message = string.IsNullOrWhiteSpace(message) ? null : Truncate(message, 2000),
            DedupeKey = Truncate(dedupeKey, 255),
            AlertId = alertId,
            BackupSetId = backupSetId,
            CreatedAt = now,
            ExpiresAt = now.AddHours(48)
        });
    }

    public async Task<IReadOnlyList<AgentNotificationDto>> ClaimForHeartbeatAsync(
        Guid clientId,
        DateTime since,
        DateTime now,
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var rows = await _db.AgentNotifications
            .FromSqlInterpolated($"""
                SELECT *
                FROM agent_notifications
                WHERE client_id = {clientId}
                  AND created_at >= {since}
                  AND expires_at > {now}
                  AND delivered_at IS NULL
                ORDER BY created_at, id
                LIMIT {Math.Clamp(limit, 1, 50)}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return [];
        }

        foreach (var row in rows)
            row.DeliveredAt = now;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return rows.Select(n => new AgentNotificationDto
        {
            Id = n.Id,
            Kind = n.Kind,
            Severity = n.Severity,
            Title = n.Title,
            Message = n.Message,
            CreatedAt = n.CreatedAt
        }).ToList();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
