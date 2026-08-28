using System.Threading.Channels;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure;

/// <summary>Infrastructure 层依赖注册（Api 层 Program.cs 调用）</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // 安全相关单例
        var jwtSettings = new JwtSettings();
        configuration.GetSection(JwtSettings.SectionName).Bind(jwtSettings);
        services.AddSingleton(jwtSettings);
        services.AddSingleton<JwtTokenService>();
        services.AddSingleton<CommandSigner>();
        services.AddSingleton<CertificateAuthority>();

        // 系统配置缓存（内部按 scope 访问数据库）
        services.AddSingleton<SystemSettingsProvider>();
        services.AddScoped<PartitionMaintenanceService>();

        // 整改批次 C · C1：密钥保护抽象，用于加密存储 SMTP 密码 / 企业微信/钉钉 webhook。
        // 密钥环持久化目录：Turnkey 模式复用 LocalServerBootstrap 已建好并加了 ACL 的
        // Server:DataDirectory；Secure/开发模式读 Security:DataProtection:KeyPath，
        // 两者都未配置时退回程序目录下的 App_Data（仅用于本地开发，不建议生产使用）。
        var dataProtectionKeyPath = FirstNonEmpty(
            configuration["Server:DataDirectory"] is { Length: > 0 } dataDir ? Path.Combine(dataDir, "DataProtection-Keys") : null,
            configuration["Security:DataProtection:KeyPath"],
            Path.Combine(AppContext.BaseDirectory, "App_Data", "DataProtection-Keys"));
        Directory.CreateDirectory(dataProtectionKeyPath);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath))
            .SetApplicationName("BackupMonitor");
        services.AddSingleton<ISecretProtector, SecretProtector>();

        // 整改批次 C · C4：令牌版本号缓存（60 秒 TTL），供 TokenVersionMiddleware 使用。
        services.AddSingleton<TokenVersionCache>();

        // 请求级服务
        services.AddScoped<ICurrentContext, HttpContextCurrentContext>();
        services.AddScoped<IAuditRecorder, DbAuditRecorder>();
        services.AddScoped<IAgentNotificationService, AgentNotificationService>();
        services.AddScoped<IAlertingService, AlertingService>();
        services.AddScoped<ICommandDispatcher, CommandService>();
        services.AddScoped<IAgentCommandService, CommandService>();
        services.AddScoped<IScheduledLockService, ScheduledLockService>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAgentRegistrationService, AgentRegistrationService>();
        services.AddScoped<LanEnrollmentService>();
        services.AddScoped<IAgentHeartbeatService, AgentHeartbeatService>();
        services.AddScoped<IAgentConfigService, AgentConfigService>();
        services.AddScoped<IAgentPrecheckService, AgentPrecheckService>();

        services.AddScoped<IUploadStorage, UploadStorage>();
        // 速度采样器必须是单例：算「现在多快」要拿这次和上一次比，
        // 按请求新建的话每次都是第一次，永远比不出增量。
        services.AddSingleton<UploadRateSampler>();
        services.AddScoped<IUploadProgressService, UploadProgressService>();
        services.AddScoped<IUploadSessionControlService, UploadSessionControlService>();
        services.AddScoped<IUploadSessionService, UploadSessionService>();
        services.AddScoped<IStorageSettingsService, StorageSettingsService>();

        services.AddScoped<IClientAdminService, ClientAdminService>();
        services.AddScoped<IMonitoredServiceAdminService, MonitoredServiceAdminService>();
        services.AddScoped<IRegistrationTokenService, RegistrationTokenService>();
        services.AddScoped<IBackupTaskService, BackupTaskService>();
        services.AddScoped<IRecognizerWizardService, RecognizerWizardService>();
        services.AddScoped<IBatchOperationService, BatchOperationService>();
        services.AddScoped<IExecutionQueueService, ExecutionQueueService>();
        services.AddScoped<IBackupPlanService, BackupPlanService>();
        services.AddScoped<IBackupSetService, BackupSetService>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();

        // 第二批接口：恢复下载 / 保留策略 / 通知 / Agent 升级 / 报表
        services.AddScoped<IRestoreService, RestoreService>();
        services.AddScoped<IRetentionPolicyService, RetentionPolicyService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAgentUpgradeService, AgentUpgradeService>();
        services.AddScoped<IReportService, ReportService>();

        // 后台队列与工作器。
        //
        // 审计 H-18：入库与校验分成两条队列。原先共享一条 SingleReader 队列顺序处理，
        // 一次几十 GB 的入库会把后面的备份集重校验和恢复请求校验全部堵住，
        // 而排队这件事在界面上完全不可见——恢复是有人在界面前等的交互操作。
        services.AddKeyedSingleton(QueueKeys.Commit, (_, _) =>
            Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true }));
        services.AddKeyedSingleton(QueueKeys.Verify, (_, _) =>
            Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true }));

        services.AddHostedService<UploadCommitWorker>();
        services.AddHostedService<VerificationWorker>();
        services.AddHostedService<NotificationDispatchWorker>();
        services.AddHostedService<RetentionCleanupWorker>();
        services.AddHostedService<PartitionMaintenanceWorker>();
        services.AddHostedService<SystemWatchdogWorker>();
        services.AddHostedService<MissedBackupWorker>();
        services.AddHostedService<LifecycleExpiryWorker>();
        services.AddHostedService<RepositoryReconcileWorker>();
        services.AddHostedService<SequentialExecutionWorker>();

        return services;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))!;
}
