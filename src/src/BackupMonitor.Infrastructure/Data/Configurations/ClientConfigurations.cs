using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupMonitor.Infrastructure.Data.Configurations;

public class ClientGroupConfiguration : IEntityTypeConfiguration<ClientGroup>
{
    public void Configure(EntityTypeBuilder<ClientGroup> builder)
    {
        builder.ToTable("client_groups");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ParentId).HasColumnName("parent_id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(e => e.DefaultBandwidthLimitKbps).HasColumnName("default_bandwidth_limit_kbps");
        builder.Property(e => e.DefaultConcurrency).HasColumnName("default_concurrency").HasDefaultValue(1);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("uq_client_groups_code");
        builder.HasIndex(e => e.ParentId).HasDatabaseName("idx_client_groups_parent_id");

        builder.HasOne(e => e.Parent)
            .WithMany(e => e.Children)
            .HasForeignKey(e => e.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RegistrationTokenConfiguration : IEntityTypeConfiguration<RegistrationToken>
{
    public void Configure(EntityTypeBuilder<RegistrationToken> builder)
    {
        builder.ToTable("registration_tokens", t =>
        {
            t.HasCheckConstraint("ck_registration_tokens_max_uses", "max_uses > 0");
            t.HasCheckConstraint("ck_registration_tokens_used_count", "used_count >= 0");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(255).IsRequired();
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.ClientGroupId).HasColumnName("client_group_id");
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.MaxUses).HasColumnName("max_uses").HasDefaultValue(1);
        builder.Property(e => e.UsedCount).HasColumnName("used_count").HasDefaultValue(0);
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        builder.HasIndex(e => e.TokenHash).HasDatabaseName("idx_registration_tokens_token_hash");
        builder.HasIndex(e => e.Status).HasDatabaseName("idx_registration_tokens_status");

        builder.HasOne(e => e.ClientGroup)
            .WithMany(e => e.RegistrationTokens)
            .HasForeignKey(e => e.ClientGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.CreatedByUser)
            .WithMany(e => e.CreatedRegistrationTokens)
            .HasForeignKey(e => e.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("clients");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.MachineId).HasColumnName("machine_id").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Hostname).HasColumnName("hostname").HasMaxLength(255).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(255).IsRequired();
        builder.Property(e => e.ClientGroupId).HasColumnName("client_group_id");
        builder.Property(e => e.OsName).HasColumnName("os_name").HasMaxLength(255);
        builder.Property(e => e.OsVersion).HasColumnName("os_version").HasMaxLength(128);
        builder.Property(e => e.Architecture).HasColumnName("architecture").HasMaxLength(32);
        builder.Property(e => e.AgentVersion).HasColumnName("agent_version").HasMaxLength(64);
        builder.Property(e => e.IpAddresses).HasColumnName("ip_addresses").HasColumnType("jsonb");
        builder.Property(e => e.LastRemoteIp).HasColumnName("last_remote_ip").HasMaxLength(64);
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.EnrollmentMode).HasColumnName("enrollment_mode").HasMaxLength(16).HasDefaultValue("secure");
        builder.Property(e => e.ApprovedAt).HasColumnName("approved_at");
        builder.Property(e => e.ApprovedBy).HasColumnName("approved_by");
        builder.Property(e => e.LastHeartbeatAt).HasColumnName("last_heartbeat_at");
        builder.Property(e => e.LastConfigVersion).HasColumnName("last_config_version").HasDefaultValue(0L);
        builder.Property(e => e.ConfigRevision).HasColumnName("config_revision").HasDefaultValue(0L);
        builder.Property(e => e.CertificateThumbprint).HasColumnName("certificate_thumbprint").HasMaxLength(128);
        builder.Property(e => e.CertificateExpiresAt).HasColumnName("certificate_expires_at");
        builder.Property(e => e.TimeOffsetSeconds).HasColumnName("time_offset_seconds");
        builder.Property(e => e.Notes).HasColumnName("notes").HasMaxLength(1000);
        builder.Property(e => e.PublicKey).HasColumnName("public_key");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();
        builder.Property(e => e.RowVersion).HasColumnName("row_version").HasDefaultValue(1L).IsConcurrencyToken();

        builder.HasIndex(e => e.MachineId).IsUnique().HasDatabaseName("uq_clients_machine_id");
        builder.HasIndex(e => new { e.Status, e.LastHeartbeatAt }).HasDatabaseName("idx_clients_status_heartbeat");
        builder.HasIndex(e => e.ClientGroupId).HasDatabaseName("idx_clients_client_group_id");
        builder.HasIndex(e => e.Hostname).HasDatabaseName("idx_clients_hostname");

        builder.HasOne(e => e.ClientGroup)
            .WithMany(e => e.Clients)
            .HasForeignKey(e => e.ClientGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.ApprovedByUser)
            .WithMany()
            .HasForeignKey(e => e.ApprovedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ClientCertificateConfiguration : IEntityTypeConfiguration<ClientCertificate>
{
    public void Configure(EntityTypeBuilder<ClientCertificate> builder)
    {
        builder.ToTable("client_certificates");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.Thumbprint).HasColumnName("thumbprint").HasMaxLength(128).IsRequired();
        builder.Property(e => e.SerialNumber).HasColumnName("serial_number").HasMaxLength(128);
        builder.Property(e => e.IssuedAt).HasColumnName("issued_at");
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        builder.Property(e => e.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(500);
        builder.Property(e => e.CertificatePem).HasColumnName("certificate_pem");

        builder.HasIndex(e => e.Thumbprint).IsUnique().HasDatabaseName("uq_client_certificates_thumbprint");
        builder.HasIndex(e => e.ClientId).HasDatabaseName("idx_client_certificates_client_id");
        builder.HasIndex(e => e.Status).HasDatabaseName("idx_client_certificates_status");

        builder.HasOne(e => e.Client)
            .WithMany(e => e.Certificates)
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>client_heartbeats 为按月范围分区表，EF 侧按普通表映射，分区由数据库管理。</summary>
public class ClientHeartbeatConfiguration : IEntityTypeConfiguration<ClientHeartbeat>
{
    public void Configure(EntityTypeBuilder<ClientHeartbeat> builder)
    {
        builder.ToTable("client_heartbeats");

        // 分区表要求分区键包含在主键中
        builder.HasKey(e => new { e.Id, e.ReceivedAt }).HasName("pk_client_heartbeats");
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()").ValueGeneratedOnAdd();
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.ReceivedAt).HasColumnName("received_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.ClientTime).HasColumnName("client_time");
        builder.Property(e => e.AgentUptimeSeconds).HasColumnName("agent_uptime_seconds");
        builder.Property(e => e.SystemUptimeSeconds).HasColumnName("system_uptime_seconds");
        builder.Property(e => e.CpuPercent).HasColumnName("cpu_percent").HasColumnType("numeric(5,2)");
        builder.Property(e => e.AgentCpuPercent).HasColumnName("agent_cpu_percent").HasColumnType("numeric(5,2)");
        builder.Property(e => e.MemoryPercent).HasColumnName("memory_percent").HasColumnType("numeric(5,2)");
        builder.Property(e => e.MemoryTotalBytes).HasColumnName("memory_total_bytes");
        builder.Property(e => e.MemoryAvailableBytes).HasColumnName("memory_available_bytes");
        builder.Property(e => e.AgentMemoryBytes).HasColumnName("agent_memory_bytes");
        builder.Property(e => e.NetworkSendBps).HasColumnName("network_send_bps");
        builder.Property(e => e.NetworkReceiveBps).HasColumnName("network_receive_bps");
        builder.Property(e => e.ActiveCommandCount).HasColumnName("active_command_count");
        builder.Property(e => e.ActiveUploadCount).HasColumnName("active_upload_count");
        builder.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");

        builder.HasIndex(e => new { e.ClientId, e.ReceivedAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_client_heartbeats_client_received");
        builder.HasIndex(e => e.ReceivedAt).HasDatabaseName("idx_client_heartbeats_received");

        builder.HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ClientDiskConfiguration : IEntityTypeConfiguration<ClientDisk>
{
    public void Configure(EntityTypeBuilder<ClientDisk> builder)
    {
        builder.ToTable("client_disks");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.DriveName).HasColumnName("drive_name").HasMaxLength(32).IsRequired();
        builder.Property(e => e.VolumeLabel).HasColumnName("volume_label").HasMaxLength(128);
        builder.Property(e => e.Filesystem).HasColumnName("filesystem").HasMaxLength(32);
        builder.Property(e => e.TotalBytes).HasColumnName("total_bytes");
        builder.Property(e => e.FreeBytes).HasColumnName("free_bytes");
        builder.Property(e => e.IsSourceVolume).HasColumnName("is_source_volume").HasDefaultValue(false);
        builder.Property(e => e.SampledAt).HasColumnName("sampled_at").HasDefaultValueSql("now()");

        builder.HasIndex(e => e.ClientId).HasDatabaseName("idx_client_disks_client_id");

        builder.HasOne(e => e.Client)
            .WithMany(e => e.Disks)
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class MonitoredServiceDefinitionConfiguration : IEntityTypeConfiguration<MonitoredServiceDefinition>
{
    public void Configure(EntityTypeBuilder<MonitoredServiceDefinition> builder)
    {
        builder.ToTable("monitored_service_definitions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.ServiceName).HasColumnName("service_name").HasMaxLength(255).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(255).IsRequired();
        builder.Property(e => e.ExpectedState).HasColumnName("expected_state");
        builder.Property(e => e.AlertOnMismatch).HasColumnName("alert_on_mismatch").HasDefaultValue(true);
        builder.Property(e => e.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        builder.Property(e => e.CurrentActualState).HasColumnName("current_actual_state");
        builder.Property(e => e.CurrentStartType).HasColumnName("current_start_type");
        builder.Property(e => e.CurrentSampledAt).HasColumnName("current_sampled_at");

        builder.HasIndex(e => e.ClientId).HasDatabaseName("idx_monitored_service_definitions_client");

        builder.HasOne(e => e.Client)
            .WithMany(e => e.MonitoredServices)
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ClientServiceStateConfiguration : IEntityTypeConfiguration<ClientServiceState>
{
    public void Configure(EntityTypeBuilder<ClientServiceState> builder)
    {
        builder.ToTable("client_service_states");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.DefinitionId).HasColumnName("definition_id");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.ActualState).HasColumnName("actual_state");
        builder.Property(e => e.StartType).HasColumnName("start_type");
        builder.Property(e => e.SampledAt).HasColumnName("sampled_at").HasDefaultValueSql("now()");

        builder.HasIndex(e => e.ClientId).HasDatabaseName("idx_client_service_states_client");
        builder.HasIndex(e => e.DefinitionId).HasDatabaseName("idx_client_service_states_definition");

        builder.HasOne(e => e.Definition)
            .WithMany(e => e.ServiceStates)
            .HasForeignKey(e => e.DefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ClientUserSessionConfiguration : IEntityTypeConfiguration<ClientUserSession>
{
    public void Configure(EntityTypeBuilder<ClientUserSession> builder)
    {
        builder.ToTable("client_user_sessions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.SessionId).HasColumnName("session_id");
        builder.Property(e => e.Username).HasColumnName("username").HasMaxLength(255);
        builder.Property(e => e.Domain).HasColumnName("domain_name").HasMaxLength(255);
        builder.Property(e => e.State).HasColumnName("state").HasMaxLength(32).IsRequired();
        builder.Property(e => e.ClientName).HasColumnName("client_name").HasMaxLength(255);
        builder.Property(e => e.ClientAddress).HasColumnName("client_address").HasMaxLength(128);
        builder.Property(e => e.IsRemote).HasColumnName("is_remote").HasDefaultValue(false);
        builder.Property(e => e.LogonAt).HasColumnName("logon_at");
        builder.Property(e => e.SampledAt).HasColumnName("sampled_at").HasDefaultValueSql("now()");

        builder.HasIndex(e => new { e.ClientId, e.SessionId })
            .IsUnique().HasDatabaseName("uq_client_user_sessions_client_session");
        builder.HasIndex(e => new { e.ClientId, e.SampledAt })
            .IsDescending(false, true).HasDatabaseName("idx_client_user_sessions_client_sampled");

        builder.HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AgentNotificationConfiguration : IEntityTypeConfiguration<AgentNotification>
{
    public void Configure(EntityTypeBuilder<AgentNotification> builder)
    {
        builder.ToTable("agent_notifications");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Severity).HasColumnName("severity").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Title).HasColumnName("title").HasMaxLength(255).IsRequired();
        builder.Property(e => e.Message).HasColumnName("message").HasMaxLength(2000);
        builder.Property(e => e.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(255).IsRequired();
        builder.Property(e => e.AlertId).HasColumnName("alert_id");
        builder.Property(e => e.BackupSetId).HasColumnName("backup_set_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.DeliveredAt).HasColumnName("delivered_at");

        builder.HasIndex(e => new { e.ClientId, e.DedupeKey })
            .IsUnique().HasDatabaseName("uq_agent_notifications_client_dedupe");
        builder.HasIndex(e => new { e.ClientId, e.CreatedAt })
            .IsDescending(false, true).HasDatabaseName("idx_agent_notifications_client_created");
        builder.HasIndex(e => e.ExpiresAt).HasDatabaseName("idx_agent_notifications_expires");
        builder.HasIndex(e => new { e.ClientId, e.DeliveredAt, e.CreatedAt })
            .HasDatabaseName("idx_agent_notifications_client_delivery");

        builder.HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<BackupMonitor.Core.Entities.Alert.Alert>()
            .WithMany()
            .HasForeignKey(e => e.AlertId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<BackupMonitor.Core.Entities.Backup.BackupSet>()
            .WithMany()
            .HasForeignKey(e => e.BackupSetId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
