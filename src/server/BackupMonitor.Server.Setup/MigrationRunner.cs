using BackupMonitor.Infrastructure.Database;

namespace BackupMonitor.Server.Setup;

/// <summary>
/// 兼容安装器原有命名空间的门面；实际迁移实现位于 Infrastructure，
/// 供安装器、API 命令行入口和测试共用同一份代码。
/// </summary>
public sealed class MigrationRunner : BackupMonitor.Infrastructure.Database.MigrationRunner
{
}
