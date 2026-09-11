using BackupMonitor.Core.Abstractions;
using BackupMonitor.Core.Entities.Alert;
using BackupMonitor.Core.Entities.Audit;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Entities.Rbac;
using BackupMonitor.Core.Entities.Restore;
using BackupMonitor.Core.Entities.Retention;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Core.Entities.Upload;
using BackupMonitor.Infrastructure.Data.Extensions;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Data;

/// <summary>
/// 轻量级集中备份采集与监控系统 - 主数据库上下文（PostgreSQL 15+）。
/// 数据库结构以 database/V001__initial_schema.sql 为准，EF Core 模型与之严格对齐。
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    #region RBAC

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    #endregion

    #region 客户端管理

    public DbSet<ClientGroup> ClientGroups => Set<ClientGroup>();
    public DbSet<RegistrationToken> RegistrationTokens => Set<RegistrationToken>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientCertificate> ClientCertificates => Set<ClientCertificate>();
    public DbSet<ClientHeartbeat> ClientHeartbeats => Set<ClientHeartbeat>();
    public DbSet<ClientDisk> ClientDisks => Set<ClientDisk>();
    public DbSet<MonitoredServiceDefinition> MonitoredServiceDefinitions => Set<MonitoredServiceDefinition>();
    public DbSet<ClientServiceState> ClientServiceStates => Set<ClientServiceState>();
    public DbSet<ClientUserSession> ClientUserSessions => Set<ClientUserSession>();
    public DbSet<AgentNotification> AgentNotifications => Set<AgentNotification>();

    #endregion

    #region 备份任务

    public DbSet<BackupTaskTemplate> BackupTaskTemplates => Set<BackupTaskTemplate>();
    public DbSet<BackupTask> BackupTasks => Set<BackupTask>();
    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();
    public DbSet<CandidateBackupSet> CandidateBackupSets => Set<CandidateBackupSet>();
    public DbSet<CandidateFile> CandidateFiles => Set<CandidateFile>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<BackupSet> BackupSets => Set<BackupSet>();
    public DbSet<BackupFile> BackupFiles => Set<BackupFile>();
    public DbSet<BackupPlan> BackupPlans => Set<BackupPlan>();
    public DbSet<BackupPlanItem> BackupPlanItems => Set<BackupPlanItem>();
    public DbSet<Core.Entities.Execution.ExecutionRun> ExecutionRuns => Set<Core.Entities.Execution.ExecutionRun>();
    public DbSet<Core.Entities.Execution.ExecutionRunItem> ExecutionRunItems => Set<Core.Entities.Execution.ExecutionRunItem>();

    #endregion

    #region 上传

    public DbSet<UploadBatch> UploadBatches => Set<UploadBatch>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<UploadFileEntity> UploadFiles => Set<UploadFileEntity>();
    public DbSet<UploadChunk> UploadChunks => Set<UploadChunk>();

    #endregion

    #region 保留与恢复

    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<RetentionLock> RetentionLocks => Set<RetentionLock>();
    public DbSet<RestoreRequest> RestoreRequests => Set<RestoreRequest>();

    #endregion

    #region 告警与审计

    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AlertSilence> AlertSilences => Set<AlertSilence>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    #endregion

    #region 系统

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<ScheduledLock> ScheduledLocks => Set<ScheduledLock>();

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    public DbSet<ConfigBackupExport> ConfigBackupExports => Set<ConfigBackupExport>();

    #endregion

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 应用本程序集中全部 IEntityTypeConfiguration
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // 全部枚举属性统一 snake_case 字符串映射（对应 PostgreSQL DOMAIN 类型）
        modelBuilder.ApplySnakeCaseEnumConverters();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        IncrementRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        IncrementRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// 乐观锁：修改含 RowVersion 的实体时自动递增版本号。
    /// EF 并发令牌会在 UPDATE 中附带 WHERE row_version = 原值，
    /// 冲突时抛出 DbUpdateConcurrencyException（API 层转换为 409 CONFLICT）。
    /// </summary>
    private void IncrementRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<IHasRowVersion>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.RowVersion++;
        }
    }
}
