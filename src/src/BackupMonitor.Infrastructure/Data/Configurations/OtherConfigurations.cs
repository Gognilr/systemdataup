using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Entities.Audit;
using BackupMonitor.Core.Entities.Restore;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupMonitor.Infrastructure.Data.Configurations;

public class RestoreRequestConfiguration : IEntityTypeConfiguration<RestoreRequest>
{
    public void Configure(EntityTypeBuilder<RestoreRequest> builder)
    {
        builder.ToTable("restore_requests");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.BackupSetId).HasColumnName("backup_set_id");
        builder.Property(e => e.RequestedBy).HasColumnName("requested_by");
        builder.Property(e => e.Purpose).HasColumnName("purpose").HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.RequestedAt).HasColumnName("requested_at").HasDefaultValueSql("now()");
        builder.Property(e => e.VerifiedAt).HasColumnName("verified_at");
        builder.Property(e => e.DownloadTokenHash).HasColumnName("download_token_hash").HasMaxLength(255);
        builder.Property(e => e.DownloadExpiresAt).HasColumnName("download_expires_at");
        builder.Property(e => e.DownloadedBytes).HasColumnName("downloaded_bytes").HasDefaultValue(0L);
        builder.Property(e => e.DeliveredPaths).HasColumnName("delivered_paths").HasColumnType("jsonb");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.ClientIp).HasColumnName("client_ip").HasMaxLength(64);
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);

        builder.HasIndex(e => e.BackupSetId).HasDatabaseName("idx_restore_requests_backup_set");
        builder.HasIndex(e => e.RequestedBy).HasDatabaseName("idx_restore_requests_requested_by");

        builder.HasOne(e => e.BackupSet)
            .WithMany()
            .HasForeignKey(e => e.BackupSetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.RequestedByUser)
            .WithMany()
            .HasForeignKey(e => e.RequestedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.ToTable("alerts");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.AlertKey).HasColumnName("alert_key").HasMaxLength(255).IsRequired();
        builder.Property(e => e.Level).HasColumnName("level");
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.Category).HasColumnName("category").HasMaxLength(64);
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(e => e.BackupSetId).HasColumnName("backup_set_id");
        builder.Property(e => e.Title).HasColumnName("title").HasMaxLength(255).IsRequired();
        builder.Property(e => e.Message).HasColumnName("message").HasMaxLength(4000);
        builder.Property(e => e.FirstOccurredAt).HasColumnName("first_occurred_at").HasDefaultValueSql("now()");
        builder.Property(e => e.LastOccurredAt).HasColumnName("last_occurred_at").HasDefaultValueSql("now()");
        builder.Property(e => e.OccurrenceCount).HasColumnName("occurrence_count").HasDefaultValue(1);
        builder.Property(e => e.LastNotifiedAt).HasColumnName("last_notified_at");
        builder.Property(e => e.AcknowledgedBy).HasColumnName("acknowledged_by");
        builder.Property(e => e.AcknowledgedAt).HasColumnName("acknowledged_at");
        builder.Property(e => e.AssignedTo).HasColumnName("assigned_to");
        builder.Property(e => e.AssignedAt).HasColumnName("assigned_at");
        builder.Property(e => e.RecoveredAt).HasColumnName("recovered_at");
        builder.Property(e => e.ClosedAt).HasColumnName("closed_at");
        builder.Property(e => e.HandlingNote).HasColumnName("handling_note").HasMaxLength(4000);
        builder.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");

        builder.HasIndex(e => new { e.Level, e.Status }).HasDatabaseName("idx_alerts_level_status");
        builder.HasIndex(e => e.ClientId).HasDatabaseName("idx_alerts_client");
        builder.HasIndex(e => e.TaskId).HasDatabaseName("idx_alerts_task");
        builder.HasIndex(e => e.LastOccurredAt).IsDescending().HasDatabaseName("idx_alerts_last_occurred");
        builder.HasIndex(e => e.AssignedTo).HasDatabaseName("idx_alerts_assigned_to");

        // 活动告警去重：同一 alert_key 在 open/acknowledged/in_progress 状态下仅一条（部分唯一索引）
        builder.HasIndex(e => e.AlertKey)
            .IsUnique()
            .HasFilter("status IN ('open', 'acknowledged', 'in_progress')")
            .HasDatabaseName("uq_alerts_active_key");

        builder.HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.BusinessUnit)
            .WithMany()
            .HasForeignKey(e => e.BusinessUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.BackupSet)
            .WithMany()
            .HasForeignKey(e => e.BackupSetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.AcknowledgedByUser)
            .WithMany()
            .HasForeignKey(e => e.AcknowledgedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.AssignedToUser)
            .WithMany()
            .HasForeignKey(e => e.AssignedTo)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class AlertSilenceConfiguration : IEntityTypeConfiguration<AlertSilence>
{
    public void Configure(EntityTypeBuilder<AlertSilence> builder)
    {
        builder.ToTable("alert_silences");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.AlertKeyPattern).HasColumnName("alert_key_pattern").HasMaxLength(255).IsRequired();
        builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Until).HasColumnName("until");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasIndex(e => e.Until).HasDatabaseName("idx_alert_silences_until");

        builder.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.AlertId).HasColumnName("alert_id");
        builder.Property(e => e.Channel).HasColumnName("channel");
        builder.Property(e => e.Recipient).HasColumnName("recipient").HasMaxLength(255).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        builder.Property(e => e.LastAttemptAt).HasColumnName("last_attempt_at");
        builder.Property(e => e.SentAt).HasColumnName("sent_at");
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);
        builder.Property(e => e.TitlePrefix).HasColumnName("title_prefix").HasMaxLength(32);
        builder.Property(e => e.Subject).HasColumnName("subject").HasMaxLength(200);
        builder.Property(e => e.Body).HasColumnName("body");

        builder.HasIndex(e => e.AlertId).HasDatabaseName("idx_notification_deliveries_alert");
        builder.HasIndex(e => e.Status).HasDatabaseName("idx_notification_deliveries_status");

        builder.HasOne(e => e.Alert)
            .WithMany(e => e.Deliveries)
            .HasForeignKey(e => e.AlertId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>audit_logs 为按月范围分区表（只追加），EF 侧按普通表映射。</summary>
public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        // 分区表要求分区键包含在主键中
        builder.HasKey(e => new { e.Id, e.OccurredAt }).HasName("pk_audit_logs");
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()").ValueGeneratedOnAdd();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UserId).HasColumnName("user_id");
        builder.Property(e => e.UsernameSnapshot).HasColumnName("username_snapshot").HasMaxLength(128);
        builder.Property(e => e.ClientIp).HasColumnName("client_ip").HasMaxLength(64);
        builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(128).IsRequired();
        builder.Property(e => e.ResourceType).HasColumnName("resource_type").HasMaxLength(64);
        builder.Property(e => e.ResourceId).HasColumnName("resource_id");
        builder.Property(e => e.RequestId).HasColumnName("request_id").HasMaxLength(128);
        builder.Property(e => e.Result).HasColumnName("result");
        builder.Property(e => e.BeforeData).HasColumnName("before_data").HasColumnType("jsonb");
        builder.Property(e => e.AfterData).HasColumnName("after_data").HasColumnType("jsonb");
        builder.Property(e => e.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);
        builder.Property(e => e.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        builder.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");

        builder.HasIndex(e => e.OccurredAt).IsDescending().HasDatabaseName("idx_audit_logs_occurred");
        builder.HasIndex(e => new { e.UserId, e.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_audit_logs_user_occurred");
        builder.HasIndex(e => new { e.ResourceType, e.ResourceId }).HasDatabaseName("idx_audit_logs_resource");
        builder.HasIndex(e => e.Action).HasDatabaseName("idx_audit_logs_action");
        builder.HasIndex(e => e.RequestId).HasDatabaseName("idx_audit_logs_request_id");

        // 注意：audit_logs.user_id 在 SQL 中不建外键（审计独立性要求），此处也不配置导航关系。
    }
}

public class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("system_settings");

        // 主键为配置键（varchar），非 UUID
        builder.HasKey(e => e.SettingKey).HasName("system_settings_pkey");
        builder.Property(e => e.SettingKey).HasColumnName("setting_key").HasMaxLength(128);
        builder.Property(e => e.SettingValue).HasColumnName("setting_value").HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.Encrypted).HasColumnName("encrypted").HasDefaultValue(false);
        builder.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();
        builder.Property(e => e.RowVersion).HasColumnName("row_version").HasDefaultValue(1L).IsConcurrencyToken();

        builder.HasOne(e => e.UpdatedByUser)
            .WithMany()
            .HasForeignKey(e => e.UpdatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>定时任务单实例锁（设计书 §25），主键为 lock_key（varchar），非 UUID。</summary>
public class ScheduledLockConfiguration : IEntityTypeConfiguration<ScheduledLock>
{
    public void Configure(EntityTypeBuilder<ScheduledLock> builder)
    {
        builder.ToTable("scheduled_locks");

        builder.HasKey(e => e.LockKey).HasName("scheduled_locks_pkey");
        builder.Property(e => e.LockKey).HasColumnName("lock_key").HasMaxLength(64);
        builder.Property(e => e.OwnerId).HasColumnName("owner_id").HasMaxLength(128).IsRequired();
        builder.Property(e => e.AcquiredAt).HasColumnName("acquired_at").HasDefaultValueSql("now()");
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
    }
}

/// <summary>通用幂等键（设计书 8.5），复合主键 (scope, idempotency_key)。</summary>
public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");

        builder.HasKey(e => new { e.Scope, e.Key }).HasName("idempotency_keys_pkey");
        builder.Property(e => e.Scope).HasColumnName("scope").HasMaxLength(64);
        builder.Property(e => e.Key).HasColumnName("idempotency_key").HasMaxLength(128);
        builder.Property(e => e.ResourceId).HasColumnName("resource_id").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();

        builder.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_idempotency_keys_expires_at");
    }
}

/// <summary>管理网页发起的配置备份包导出（V036 · 整改清单 R4）。</summary>
public class ConfigBackupExportConfiguration : IEntityTypeConfiguration<ConfigBackupExport>
{
    public void Configure(EntityTypeBuilder<ConfigBackupExport> builder)
    {
        builder.ToTable("config_backup_exports");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
        builder.Property(e => e.FilePath).HasColumnName("file_path");
        builder.Property(e => e.FileName).HasColumnName("file_name").HasMaxLength(255);
        builder.Property(e => e.SizeBytes).HasColumnName("size_bytes");
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message");
        builder.Property(e => e.StartedAt).HasColumnName("started_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.RequestedBy).HasColumnName("requested_by");
        builder.Property(e => e.RequestedByName).HasColumnName("requested_by_name").HasMaxLength(100);
        builder.Property(e => e.DownloadTokenHash).HasColumnName("download_token_hash").HasMaxLength(255);
        builder.Property(e => e.DownloadExpiresAt).HasColumnName("download_expires_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        builder.HasIndex(e => e.StartedAt).HasDatabaseName("idx_config_backup_exports_started");
    }
}
