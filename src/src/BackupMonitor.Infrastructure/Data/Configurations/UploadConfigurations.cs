using BackupMonitor.Core.Entities.Upload;
using BackupMonitor.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupMonitor.Infrastructure.Data.Configurations;

public class UploadBatchConfiguration : IEntityTypeConfiguration<UploadBatch>
{
    public void Configure(EntityTypeBuilder<UploadBatch> builder)
    {
        builder.ToTable("upload_batches");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(255);
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");
        builder.Property(e => e.MaxConcurrentClients).HasColumnName("max_concurrent_clients").HasDefaultValue(2);
        builder.Property(e => e.MaxConcurrentPerClient).HasColumnName("max_concurrent_per_client").HasDefaultValue(1);
        builder.Property(e => e.BandwidthLimitKbps).HasColumnName("bandwidth_limit_kbps");
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.TotalItems).HasColumnName("total_items").HasDefaultValue(0);
        builder.Property(e => e.SucceededItems).HasColumnName("succeeded_items").HasDefaultValue(0);
        builder.Property(e => e.FailedItems).HasColumnName("failed_items").HasDefaultValue(0);
        builder.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");

        builder.HasIndex(e => e.IdempotencyKey).IsUnique().HasDatabaseName("uq_upload_batches_idempotency");

        builder.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class UploadSessionConfiguration : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> builder)
    {
        builder.ToTable("upload_sessions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.UploadBatchId).HasColumnName("upload_batch_id");
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.CandidateBackupSetId).HasColumnName("candidate_backup_set_id");
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.TotalFiles).HasColumnName("total_files");
        builder.Property(e => e.TotalBytes).HasColumnName("total_bytes");
        builder.Property(e => e.UploadedBytes).HasColumnName("uploaded_bytes").HasDefaultValue(0L);
        builder.Property(e => e.VerifiedBytes).HasColumnName("verified_bytes").HasDefaultValue(0L);
        builder.Property(e => e.ChunkSizeBytes).HasColumnName("chunk_size_bytes").HasDefaultValue(8388608);
        builder.Property(e => e.StagingPath).HasColumnName("staging_path").HasMaxLength(2048);
        builder.Property(e => e.StartedAt).HasColumnName("started_at");
        builder.Property(e => e.LastActivityAt).HasColumnName("last_activity_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.VerifiedAt).HasColumnName("verified_at");
        builder.Property(e => e.CommittedAt).HasColumnName("committed_at");
        builder.Property(e => e.RetryCount).HasColumnName("retry_count").HasDefaultValue(0);
        builder.Property(e => e.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);
        builder.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(e => e.IdempotencyKey).IsUnique().HasDatabaseName("uq_upload_sessions_idempotency");
        builder.HasIndex(e => new { e.ClientId, e.Status }).HasDatabaseName("idx_upload_sessions_client_status");
        builder.HasIndex(e => new { e.TaskId, e.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_upload_sessions_task_created");
        builder.HasIndex(e => e.CandidateBackupSetId).HasDatabaseName("idx_upload_sessions_candidate");
        builder.HasIndex(e => e.LastActivityAt).HasDatabaseName("idx_upload_sessions_last_activity");

        builder.HasOne(e => e.UploadBatch)
            .WithMany(e => e.UploadSessions)
            .HasForeignKey(e => e.UploadBatchId)
            .OnDelete(DeleteBehavior.Restrict);

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
    }
}

public class UploadFileConfiguration : IEntityTypeConfiguration<UploadFileEntity>
{
    public void Configure(EntityTypeBuilder<UploadFileEntity> builder)
    {
        builder.ToTable("upload_files");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.UploadSessionId).HasColumnName("upload_session_id");
        builder.Property(e => e.CandidateFileId).HasColumnName("candidate_file_id");
        builder.Property(e => e.RelativePath).HasColumnName("relative_path").HasMaxLength(4096).IsRequired();
        builder.Property(e => e.SizeBytes).HasColumnName("size_bytes");
        builder.Property(e => e.ExpectedSha256).HasColumnName("expected_sha256").HasMaxLength(64).IsRequired();
        builder.Property(e => e.ServerSha256).HasColumnName("server_sha256").HasMaxLength(64);
        builder.Property(e => e.UploadedBytes).HasColumnName("uploaded_bytes").HasDefaultValue(0L);
        builder.Property(e => e.TotalChunks).HasColumnName("total_chunks");
        builder.Property(e => e.UploadedChunks).HasColumnName("uploaded_chunks").HasDefaultValue(0);
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.TempPath).HasColumnName("temp_path").HasMaxLength(2048);
        builder.Property(e => e.ErrorCode).HasColumnName("error_code").HasMaxLength(64);

        builder.HasIndex(e => new { e.UploadSessionId, e.RelativePath })
            .IsUnique().HasDatabaseName("uq_upload_files_session_path");
        builder.HasIndex(e => e.UploadSessionId).HasDatabaseName("idx_upload_files_session_id");

        builder.HasOne(e => e.UploadSession)
            .WithMany(e => e.Files)
            .HasForeignKey(e => e.UploadSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.CandidateFile)
            .WithMany()
            .HasForeignKey(e => e.CandidateFileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class UploadChunkConfiguration : IEntityTypeConfiguration<UploadChunk>
{
    public void Configure(EntityTypeBuilder<UploadChunk> builder)
    {
        builder.ToTable("upload_chunks");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.UploadFileId).HasColumnName("upload_file_id");
        builder.Property(e => e.ChunkIndex).HasColumnName("chunk_index");
        builder.Property(e => e.OffsetBytes).HasColumnName("offset_bytes");
        builder.Property(e => e.SizeBytes).HasColumnName("size_bytes");
        builder.Property(e => e.ExpectedHash).HasColumnName("expected_hash").HasMaxLength(64).IsRequired();
        builder.Property(e => e.ServerHash).HasColumnName("server_hash").HasMaxLength(64);
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.ReceivedAt).HasColumnName("received_at");

        builder.HasIndex(e => new { e.UploadFileId, e.ChunkIndex })
            .IsUnique().HasDatabaseName("uq_upload_chunks_file_index");
        builder.HasIndex(e => e.UploadFileId).HasDatabaseName("idx_upload_chunks_file_id");

        builder.HasOne(e => e.UploadFile)
            .WithMany(e => e.Chunks)
            .HasForeignKey(e => e.UploadFileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
