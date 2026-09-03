using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupMonitor.Infrastructure.Data.Configurations;

public class BackupTaskTemplateConfiguration : IEntityTypeConfiguration<BackupTaskTemplate>
{
    public void Configure(EntityTypeBuilder<BackupTaskTemplate> builder)
    {
        builder.ToTable("backup_task_templates");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.RecognizerType).HasColumnName("recognizer_type");
        builder.Property(e => e.DefaultConfig).HasColumnName("default_config").HasColumnType("jsonb");
        builder.Property(e => e.IsSystem).HasColumnName("is_system").HasDefaultValue(false);
        builder.Property(e => e.Version).HasColumnName("version").HasDefaultValue(1);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("uq_backup_task_templates_code");
    }
}

public class BackupTaskConfiguration : IEntityTypeConfiguration<BackupTask>
{
    public void Configure(EntityTypeBuilder<BackupTask> builder)
    {
        builder.ToTable("backup_tasks", t =>
        {
            t.HasCheckConstraint("ck_backup_tasks_chunk_size", "chunk_size_bytes BETWEEN 4194304 AND 33554432");
            t.HasCheckConstraint("ck_backup_tasks_priority", "priority BETWEEN 1 AND 1000");
            t.HasCheckConstraint("ck_backup_tasks_stability", "stability_interval_seconds >= 60");
            t.HasCheckConstraint("ck_backup_tasks_retry", "retry_count >= 0");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.TemplateId).HasColumnName("template_id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(e => e.ApplicationName).HasColumnName("application_name").HasMaxLength(255).IsRequired();
        builder.Property(e => e.SourcePath).HasColumnName("source_path").HasMaxLength(2048).IsRequired();
        builder.Property(e => e.RecognizerType).HasColumnName("recognizer_type");
        // 默认值语义放在实体初始化器（BackupTask.TaskMode = ApprovalRequired），
        // 此处不配置 HasDefaultValue，否则显式写入 0 号成员 Automatic 会被 EF 当作"未设置"而被数据库默认值覆盖。
        builder.Property(e => e.TaskMode).HasColumnName("task_mode");
        builder.Property(e => e.PreviousTaskMode).HasColumnName("previous_task_mode");
        builder.Property(e => e.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        builder.Property(e => e.Priority).HasColumnName("priority").HasDefaultValue(100);
        builder.Property(e => e.ImportanceLevel).HasColumnName("importance_level");
        builder.Property(e => e.ScanSchedule).HasColumnName("scan_schedule").HasMaxLength(255);
        builder.Property(e => e.UploadWindowStart).HasColumnName("upload_window_start").HasColumnType("time");
        builder.Property(e => e.UploadWindowEnd).HasColumnName("upload_window_end").HasColumnType("time");
        builder.Property(e => e.ScheduleTimezone).HasColumnName("schedule_timezone").HasMaxLength(64).HasDefaultValue("Asia/Shanghai");
        builder.Property(e => e.RandomDelayMinutes).HasColumnName("random_delay_minutes").HasDefaultValue(0);
        builder.Property(e => e.StabilityIntervalSeconds).HasColumnName("stability_interval_seconds").HasDefaultValue(600);
        builder.Property(e => e.MaxStabilityWaitSeconds).HasColumnName("max_stability_wait_seconds").HasDefaultValue(7200);
        builder.Property(e => e.MinTotalBytes).HasColumnName("min_total_bytes");
        builder.Property(e => e.MaxTotalBytes).HasColumnName("max_total_bytes");
        builder.Property(e => e.MinFileCount).HasColumnName("min_file_count");
        builder.Property(e => e.BandwidthLimitKbps).HasColumnName("bandwidth_limit_kbps");
        builder.Property(e => e.ChunkSizeBytes).HasColumnName("chunk_size_bytes").HasDefaultValue(8388608);
        builder.Property(e => e.RetryCount).HasColumnName("retry_count").HasDefaultValue(3);
        builder.Property(e => e.RetryIntervalSeconds).HasColumnName("retry_interval_seconds").HasDefaultValue(1800);
        builder.Property(e => e.RetentionPolicyId).HasColumnName("retention_policy_id");
        builder.Property(e => e.ConfigVersion).HasColumnName("config_version").HasDefaultValue(1L);
        builder.Property(e => e.RecognizerConfig).HasColumnName("recognizer_config").HasColumnType("jsonb").HasDefaultValue("{}");
        builder.Property(e => e.AlertConfig).HasColumnName("alert_config").HasColumnType("jsonb");
        builder.Property(e => e.LastScanAt).HasColumnName("last_scan_at");
        builder.Property(e => e.LastPrecheckStatus).HasColumnName("last_precheck_status");
        builder.Property(e => e.LastSuccessAt).HasColumnName("last_success_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();
        builder.Property(e => e.RowVersion).HasColumnName("row_version").HasDefaultValue(1L).IsConcurrencyToken();

        builder.HasIndex(e => new { e.ClientId, e.Enabled }).HasDatabaseName("idx_backup_tasks_client_enabled");
        builder.HasIndex(e => new { e.TaskMode, e.Enabled }).HasDatabaseName("idx_backup_tasks_mode_enabled");
        builder.HasIndex(e => e.LastSuccessAt).HasDatabaseName("idx_backup_tasks_last_success");
        builder.HasIndex(e => e.ApplicationName).HasDatabaseName("idx_backup_tasks_application");

        builder.HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Template)
            .WithMany(e => e.BackupTasks)
            .HasForeignKey(e => e.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.RetentionPolicy)
            .WithMany(e => e.BackupTasks)
            .HasForeignKey(e => e.RetentionPolicyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class BusinessUnitConfiguration : IEntityTypeConfiguration<BusinessUnit>
{
    public void Configure(EntityTypeBuilder<BusinessUnit> builder)
    {
        builder.ToTable("business_units");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.ExternalKey).HasColumnName("external_key").HasMaxLength(255).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(255).IsRequired();
        builder.Property(e => e.SourceRelativePath).HasColumnName("source_relative_path").HasMaxLength(2048);
        builder.Property(e => e.Expected).HasColumnName("expected").HasDefaultValue(true);
        builder.Property(e => e.Ignored).HasColumnName("ignored").HasDefaultValue(false);
        builder.Property(e => e.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        builder.Property(e => e.LastDiscoveredAt).HasColumnName("last_discovered_at");
        builder.Property(e => e.LastSuccessAt).HasColumnName("last_success_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(e => new { e.TaskId, e.ExternalKey })
            .IsUnique().HasDatabaseName("uq_business_units_task_key");
        builder.HasIndex(e => e.TaskId).HasDatabaseName("idx_business_units_task_id");

        builder.HasOne(e => e.Task)
            .WithMany(e => e.BusinessUnits)
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class CandidateBackupSetConfiguration : IEntityTypeConfiguration<CandidateBackupSet>
{
    public void Configure(EntityTypeBuilder<CandidateBackupSet> builder)
    {
        builder.ToTable("candidate_backup_sets");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(e => e.CandidateKey).HasColumnName("candidate_key").HasMaxLength(512).IsRequired();
        builder.Property(e => e.SourceRoot).HasColumnName("source_root").HasMaxLength(2048).IsRequired();
        builder.Property(e => e.BackupBusinessTime).HasColumnName("backup_business_time");
        builder.Property(e => e.DiscoveredAt).HasColumnName("discovered_at").HasDefaultValueSql("now()");
        builder.Property(e => e.PrecheckedAt).HasColumnName("prechecked_at");
        builder.Property(e => e.PrecheckStatus).HasColumnName("precheck_status");
        builder.Property(e => e.TotalFiles).HasColumnName("total_files");
        builder.Property(e => e.TotalBytes).HasColumnName("total_bytes");
        builder.Property(e => e.ManifestHash).HasColumnName("manifest_hash").HasMaxLength(64);
        builder.Property(e => e.QuickFingerprint).HasColumnName("quick_fingerprint").HasMaxLength(128);
        builder.Property(e => e.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        builder.Property(e => e.FailureMessage).HasColumnName("failure_message").HasMaxLength(2000);
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.SupersededById).HasColumnName("superseded_by_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(e => new { e.TaskId, e.DiscoveredAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_candidate_sets_task_discovered");
        builder.HasIndex(e => new { e.TaskId, e.BusinessUnitId, e.BackupBusinessTime })
            .IsDescending(false, false, true)
            .HasDatabaseName("idx_candidate_sets_task_bu_time");
        builder.HasIndex(e => e.PrecheckStatus).HasDatabaseName("idx_candidate_sets_precheck_status");
        builder.HasIndex(e => e.CandidateKey).HasDatabaseName("idx_candidate_sets_candidate_key");

        builder.HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Task)
            .WithMany(e => e.CandidateBackupSets)
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.BusinessUnit)
            .WithMany()
            .HasForeignKey(e => e.BusinessUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.SupersededBy)
            .WithMany()
            .HasForeignKey(e => e.SupersededById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class CandidateFileConfiguration : IEntityTypeConfiguration<CandidateFile>
{
    public void Configure(EntityTypeBuilder<CandidateFile> builder)
    {
        builder.ToTable("candidate_files");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.CandidateBackupSetId).HasColumnName("candidate_backup_set_id");
        builder.Property(e => e.RelativePath).HasColumnName("relative_path").HasMaxLength(4096).IsRequired();
        builder.Property(e => e.FileName).HasColumnName("file_name").HasMaxLength(1024).IsRequired();
        builder.Property(e => e.SizeBytes).HasColumnName("size_bytes");
        builder.Property(e => e.LastModifiedAt).HasColumnName("last_modified_at");
        builder.Property(e => e.Sha256).HasColumnName("sha256").HasMaxLength(64);
        builder.Property(e => e.QuickHash).HasColumnName("quick_hash").HasMaxLength(64);
        builder.Property(e => e.IsRequired).HasColumnName("is_required").HasDefaultValue(false);
        builder.Property(e => e.SortOrder).HasColumnName("sort_order").HasDefaultValue(0);

        builder.HasIndex(e => new { e.CandidateBackupSetId, e.RelativePath })
            .IsUnique().HasDatabaseName("uq_candidate_files_set_path");
        builder.HasIndex(e => e.CandidateBackupSetId).HasDatabaseName("idx_candidate_files_set_id");

        builder.HasOne(e => e.CandidateBackupSet)
            .WithMany(e => e.Files)
            .HasForeignKey(e => e.CandidateBackupSetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class CommandConfiguration : IEntityTypeConfiguration<Command>
{
    public void Configure(EntityTypeBuilder<Command> builder)
    {
        builder.ToTable("commands");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.CandidateBackupSetId).HasColumnName("candidate_backup_set_id");
        builder.Property(e => e.CommandType).HasColumnName("command_type");
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.Priority).HasColumnName("priority").HasDefaultValue(100);
        builder.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.Property(e => e.Nonce).HasColumnName("nonce").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Signature).HasColumnName("signature").HasColumnType("text");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.ClaimedAt).HasColumnName("claimed_at");
        builder.Property(e => e.StartedAt).HasColumnName("started_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.ResultCode).HasColumnName("result_code").HasMaxLength(64);
        builder.Property(e => e.ResultMessage).HasColumnName("result_message").HasMaxLength(2000);
        builder.Property(e => e.ResultPayload).HasColumnName("result_payload").HasColumnType("jsonb");
        builder.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);

        builder.HasIndex(e => e.Nonce).IsUnique().HasDatabaseName("uq_commands_nonce");
        builder.HasIndex(e => e.IdempotencyKey).IsUnique().HasDatabaseName("uq_commands_idempotency");
        builder.HasIndex(e => new { e.ClientId, e.Status, e.Priority, e.CreatedAt })
            .HasDatabaseName("idx_commands_client_status_priority");
        builder.HasIndex(e => e.ExpiresAt).HasDatabaseName("idx_commands_expires_at");
        builder.HasIndex(e => e.CommandType).HasDatabaseName("idx_commands_command_type");

        builder.HasOne(e => e.Client)
            .WithMany()
            .HasForeignKey(e => e.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.CandidateBackupSet)
            .WithMany()
            .HasForeignKey(e => e.CandidateBackupSetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class BackupSetConfiguration : IEntityTypeConfiguration<BackupSet>
{
    public void Configure(EntityTypeBuilder<BackupSet> builder)
    {
        builder.ToTable("backup_sets");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(e => e.SourceCandidateId).HasColumnName("source_candidate_id");
        builder.Property(e => e.UploadSessionId).HasColumnName("upload_session_id");
        builder.Property(e => e.BackupSetCode).HasColumnName("backup_set_code").HasMaxLength(255).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.BackupBusinessTime).HasColumnName("backup_business_time");
        builder.Property(e => e.DiscoveredAt).HasColumnName("discovered_at");
        builder.Property(e => e.UploadedAt).HasColumnName("uploaded_at");
        builder.Property(e => e.VerifiedAt).HasColumnName("verified_at");
        builder.Property(e => e.RepositoryPath).HasColumnName("repository_path").HasMaxLength(2048);
        builder.Property(e => e.ManifestPath).HasColumnName("manifest_path").HasMaxLength(2048);
        builder.Property(e => e.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
        builder.Property(e => e.TotalFiles).HasColumnName("total_files");
        builder.Property(e => e.TotalBytes).HasColumnName("total_bytes");
        builder.Property(e => e.Locked).HasColumnName("locked").HasDefaultValue(false);
        builder.Property(e => e.RetentionUntil).HasColumnName("retention_until");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        builder.HasIndex(e => e.BackupSetCode).IsUnique().HasDatabaseName("uq_backup_sets_code");
        // V033：只有「还活着」的版本参与唯一性判定。无条件唯一时，删掉一份备份之后
        // 源文件没变就再也备不回来——软删的行仍然占着这个候选的位置。
        // 过滤条件与 BackupSetStatuses.Live 是同一个口径，改一边就会对不上。
        builder.HasIndex(e => e.SourceCandidateId)
            .IsUnique()
            .HasDatabaseName("uq_backup_sets_candidate_live")
            .HasFilter("status NOT IN ('recycle_bin', 'deleted')");
        builder.HasIndex(e => new { e.TaskId, e.BusinessUnitId, e.BackupBusinessTime })
            .IsDescending(false, false, true)
            .HasDatabaseName("idx_backup_sets_task_bu_time");
        builder.HasIndex(e => new { e.ClientId, e.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_backup_sets_client_created");
        builder.HasIndex(e => e.Status).HasDatabaseName("idx_backup_sets_status");
        builder.HasIndex(e => e.RetentionUntil).HasDatabaseName("idx_backup_sets_retention");

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

        builder.HasOne(e => e.SourceCandidate)
            .WithMany()
            .HasForeignKey(e => e.SourceCandidateId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.UploadSession)
            .WithMany()
            .HasForeignKey(e => e.UploadSessionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class BackupFileConfiguration : IEntityTypeConfiguration<BackupFile>
{
    public void Configure(EntityTypeBuilder<BackupFile> builder)
    {
        builder.ToTable("backup_files");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.BackupSetId).HasColumnName("backup_set_id");
        builder.Property(e => e.RelativePath).HasColumnName("relative_path").HasMaxLength(4096).IsRequired();
        builder.Property(e => e.FileName).HasColumnName("file_name").HasMaxLength(1024).IsRequired();
        builder.Property(e => e.SizeBytes).HasColumnName("size_bytes");
        builder.Property(e => e.LastModifiedAt).HasColumnName("last_modified_at");
        builder.Property(e => e.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
        builder.Property(e => e.RepositoryRelativePath).HasColumnName("repository_relative_path").HasMaxLength(4096).IsRequired();
        builder.Property(e => e.VerificationStatus).HasColumnName("verification_status");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        builder.HasIndex(e => new { e.BackupSetId, e.RelativePath })
            .IsUnique().HasDatabaseName("uq_backup_files_set_path");
        builder.HasIndex(e => e.BackupSetId).HasDatabaseName("idx_backup_files_set_id");

        builder.HasOne(e => e.BackupSet)
            .WithMany(e => e.Files)
            .HasForeignKey(e => e.BackupSetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
