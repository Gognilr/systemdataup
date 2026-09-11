namespace BackupMonitor.Core.Entities.System;

/// <summary>
/// 通过管理网页发起的一次配置备份包导出，对应 config_backup_exports 表。
///
/// 服务管理台本机导出的包不经过 API，不会在这里留行——
/// 「上次配置备份是什么时候」的权威来源是 config-backups 目录里最新的那个文件，
/// 不是这张表。这张表存在的意义是留下失败原因，以及承载一次性下载令牌。
/// </summary>
public class ConfigBackupExport
{
    public Guid Id { get; set; }

    public Core.Enums.ConfigBackupExportStatus Status { get; set; }

    /// <summary>导出文件的绝对路径。失败时为空。</summary>
    public string? FilePath { get; set; }

    public string? FileName { get; set; }

    public long? SizeBytes { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public Guid? RequestedBy { get; set; }

    public string? RequestedByName { get; set; }

    /// <summary>下载令牌的 SHA-256；明文只在签发响应里出现一次。</summary>
    public string? DownloadTokenHash { get; set; }

    public DateTime? DownloadExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
