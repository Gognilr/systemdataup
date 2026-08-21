using System.Threading.Channels;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
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
        services.AddScoped<IUploadSessionService, UploadSessionService>();

        services.AddScoped<IClientAdminService, ClientAdminService>();
        services.AddScoped<IRegistrationTokenService, RegistrationTokenService>();
        services.AddScoped<IBackupTaskService, BackupTaskService>();
        services.AddScoped<IBatchOperationService, BatchOperationService>();
        services.AddScoped<IBackupSetService, BackupSetService>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();

        // 第二批接口：恢复下载 / 保留策略 / 通知 / Agent 升级 / 报表
        services.AddScoped<IRestoreService, RestoreService>();
        services.AddScoped<IRetentionPolicyService, RetentionPolicyService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAgentUpgradeService, AgentUpgradeService>();
        services.AddScoped<IReportService, ReportService>();

        // 校验入库队列与后台工作器
        services.AddSingleton(Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true
        }));
        services.AddHostedService<UploadCommitWorker>();
        services.AddHostedService<NotificationDispatchWorker>();
        services.AddHostedService<RetentionCleanupWorker>();
        services.AddHostedService<PartitionMaintenanceWorker>();

        return services;
    }
}
