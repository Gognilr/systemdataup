using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackupMonitor.Api.Health;

/// <summary>
/// 数据库健康检查（OPEN-ISSUES #6）：只读连通性自检——
/// SELECT version() 验证原生连接，再只读统计 system_settings / roles 验证 EF 模型与库结构可达。
/// 不再向 system_settings 写探针行；乐观锁与枚举映射自检已迁入测试工程（DatabaseSelfCheckTests）。
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;
    private readonly PartitionMaintenanceService _partitions;

    public DatabaseHealthCheck(AppDbContext db, PartitionMaintenanceService partitions)
    {
        _db = db;
        _partitions = partitions;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await _db.Database
                .SqlQueryRaw<string>("SELECT version() AS \"Value\"")
                .FirstAsync(cancellationToken);

            var settingsCount = await _db.SystemSettings.AsNoTracking().CountAsync(cancellationToken);
            var roleCount = await _db.Roles.AsNoTracking().CountAsync(cancellationToken);
            var missing = await _partitions.GetMissingPartitionsAsync(
                _db,
                DateTime.UtcNow,
                horizonDays: 30,
                cancellationToken);

            if (missing.Count > 0)
            {
                return HealthCheckResult.Degraded(
                    $"数据库连接正常，但未来 30 天缺少分区：{string.Join(", ", missing)}；{version}");
            }

            return HealthCheckResult.Healthy(
                $"连接正常；system_settings={settingsCount}，roles={roleCount}；{version}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
