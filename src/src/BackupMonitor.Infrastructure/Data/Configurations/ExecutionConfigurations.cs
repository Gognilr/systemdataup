using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupMonitor.Infrastructure.Data.Configurations;

/// <summary>备份计划与顺序执行队列（V028）</summary>
public class BackupPlanConfiguration : IEntityTypeConfiguration<BackupPlan>
{
    public void Configure(EntityTypeBuilder<BackupPlan> builder)
    {
        builder.ToTable("backup_plans");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        builder.Property(e => e.NotifyOnFinish).HasColumnName("notify_on_finish").HasDefaultValue(false);
        builder.Property(e => e.ScheduleKind).HasColumnName("schedule_kind");
        builder.Property(e => e.RunAt).HasColumnName("run_at").HasColumnType("time");
        builder.Property(e => e.DaysOfWeek).HasColumnName("days_of_week").HasMaxLength(32);
        builder.Property(e => e.Timezone).HasColumnName("timezone").HasMaxLength(64).HasDefaultValue("Asia/Shanghai");
        builder.Property(e => e.MaxConcurrent).HasColumnName("max_concurrent").HasDefaultValue(1);
        builder.Property(e => e.ItemTimeoutMinutes).HasColumnName("item_timeout_minutes").HasDefaultValue(240);
        builder.Property(e => e.LastRunAt).HasColumnName("last_run_at");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(e => e.Name).IsUnique().HasDatabaseName("uq_backup_plans_name");

        builder.HasMany(e => e.Items)
            .WithOne(i => i.Plan)
            .HasForeignKey(i => i.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class BackupPlanItemConfiguration : IEntityTypeConfiguration<BackupPlanItem>
{
    public void Configure(EntityTypeBuilder<BackupPlanItem> builder)
    {
        builder.ToTable("backup_plan_items");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.PlanId).HasColumnName("plan_id");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.SortOrder).HasColumnName("sort_order").HasDefaultValue(0);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        // 一个任务只能属于一个计划
        builder.HasIndex(e => e.TaskId).IsUnique().HasDatabaseName("uq_backup_plan_items_task");

        builder.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ExecutionRunConfiguration : IEntityTypeConfiguration<ExecutionRun>
{
    public void Configure(EntityTypeBuilder<ExecutionRun> builder)
    {
        builder.ToTable("execution_runs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Kind).HasColumnName("kind");
        builder.Property(e => e.PlanId).HasColumnName("plan_id");
        builder.Property(e => e.UploadBatchId).HasColumnName("upload_batch_id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(255);
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.MaxConcurrent).HasColumnName("max_concurrent").HasDefaultValue(1);
        builder.Property(e => e.ItemTimeoutMinutes).HasColumnName("item_timeout_minutes").HasDefaultValue(240);
        builder.Property(e => e.TriggerSource).HasColumnName("trigger_source").HasMaxLength(32).HasDefaultValue("schedule");
        builder.Property(e => e.TotalItems).HasColumnName("total_items").HasDefaultValue(0);
        builder.Property(e => e.SucceededItems).HasColumnName("succeeded_items").HasDefaultValue(0);
        builder.Property(e => e.FailedItems).HasColumnName("failed_items").HasDefaultValue(0);
        builder.Property(e => e.TriggeredBy).HasColumnName("triggered_by");
        builder.Property(e => e.ScheduledFor).HasColumnName("scheduled_for");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.StartedAt).HasColumnName("started_at");
        builder.Property(e => e.FinishedAt).HasColumnName("finished_at");

        builder.HasOne(e => e.Plan)
            .WithMany()
            .HasForeignKey(e => e.PlanId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(e => e.Items)
            .WithOne(i => i.Run)
            .HasForeignKey(i => i.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ExecutionRunItemConfiguration : IEntityTypeConfiguration<ExecutionRunItem>
{
    public void Configure(EntityTypeBuilder<ExecutionRunItem> builder)
    {
        builder.ToTable("execution_run_items");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.RunId).HasColumnName("run_id");
        builder.Property(e => e.SortOrder).HasColumnName("sort_order").HasDefaultValue(0);
        builder.Property(e => e.ClientId).HasColumnName("client_id");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.CandidateBackupSetId).HasColumnName("candidate_backup_set_id");
        builder.Property(e => e.CommandType).HasColumnName("command_type");
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.CommandId).HasColumnName("command_id");
        builder.Property(e => e.UploadSessionId).HasColumnName("upload_session_id");
        builder.Property(e => e.StartedAt).HasColumnName("started_at");
        builder.Property(e => e.FinishedAt).HasColumnName("finished_at");
        builder.Property(e => e.Message).HasColumnName("message").HasMaxLength(1000);

        builder.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
