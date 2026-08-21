using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Tests;

[Collection("postgres")]
public sealed class AgentNotificationDeliveryTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public AgentNotificationDeliveryTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Heartbeat_claim_marks_notification_and_does_not_redeliver()
    {
        var clientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = CreateContext())
        {
            db.Clients.Add(new Client
            {
                Id = clientId,
                MachineId = $"notification-{clientId:N}",
                Hostname = "notification-client",
                DisplayName = "notification-client",
                Status = ClientStatus.Online
            });
            db.AgentNotifications.Add(new AgentNotification
            {
                Id = notificationId,
                ClientId = clientId,
                Kind = "alert",
                Severity = "warning",
                Title = "一次性通知",
                DedupeKey = $"test:{notificationId:N}",
                CreatedAt = now.AddMinutes(-1),
                ExpiresAt = now.AddHours(1)
            });
            await db.SaveChangesAsync();
        }

        try
        {
            await using var db = CreateContext();
            var service = new AgentNotificationService(db);
            var first = await service.ClaimForHeartbeatAsync(
                clientId,
                now.AddHours(-1),
                now,
                limit: 20,
                CancellationToken.None);
            var second = await service.ClaimForHeartbeatAsync(
                clientId,
                now.AddHours(-1),
                now.AddSeconds(1),
                limit: 20,
                CancellationToken.None);

            var item = Assert.Single(first);
            Assert.Equal(notificationId, item.Id);
            Assert.Empty(second);
            var deliveredAt = await db.AgentNotifications
                .Where(n => n.Id == notificationId)
                .Select(n => n.DeliveredAt)
                .SingleAsync();
            Assert.NotNull(deliveredAt);
        }
        finally
        {
            await using var cleanup = CreateContext();
            await cleanup.AgentNotifications
                .Where(n => n.Id == notificationId)
                .ExecuteDeleteAsync();
            await cleanup.Clients
                .Where(c => c.Id == clientId)
                .ExecuteDeleteAsync();
        }
    }

    private AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options);
}
