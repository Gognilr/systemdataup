using BackupMonitor.Core.Entities.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupMonitor.Infrastructure.Data.Configurations;

public class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> builder)
    {
        builder.ToTable("retention_policies", t =>
        {
            t.HasCheckConstraint("ck_retention_policies_counts", "keep_last_count IS NULL OR keep_last_count > 0");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.KeepLastCount).HasColumnName("keep_last_count");
        builder.Property(e => e.KeepWeeklyCount).HasColumnName("keep_weekly_count");
        builder.Property(e => e.KeepMonthlyCount).HasColumnName("keep_monthly_count");
        builder.Property(e => e.KeepYearlyCount).HasColumnName("keep_yearly_count");
        builder.Property(e => e.MinimumRetentionDays).HasColumnName("minimum_retention_days").HasDefaultValue(30);
        builder.Property(e => e.RecycleBinDays).HasColumnName("recycle_bin_days").HasDefaultValue(30);
        builder.Property(e => e.Config).HasColumnName("config").HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();
    }
}

public class RetentionLockConfiguration : IEntityTypeConfiguration<RetentionLock>
{
    public void Configure(EntityTypeBuilder<RetentionLock> builder)
    {
        builder.ToTable("retention_locks");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.BackupSetId).HasColumnName("backup_set_id");
        builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
        builder.Property(e => e.LockedBy).HasColumnName("locked_by");
        builder.Property(e => e.LockedAt).HasColumnName("locked_at").HasDefaultValueSql("now()");
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.Active).HasColumnName("active").HasDefaultValue(true);

        builder.HasIndex(e => e.BackupSetId).HasDatabaseName("idx_retention_locks_backup_set");
        builder.HasIndex(e => e.Active)
            .HasFilter("active = true")
            .HasDatabaseName("idx_retention_locks_active");

        builder.HasOne(e => e.BackupSet)
            .WithMany(e => e.RetentionLocks)
            .HasForeignKey(e => e.BackupSetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.LockedByUser)
            .WithMany()
            .HasForeignKey(e => e.LockedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
