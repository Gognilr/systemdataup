namespace BackupMonitor.Shared.Models.Admin;

/// <summary>
/// 配置备份包的现状（整改清单 R4）。
///
/// 「上次导出时间」取自 config-backups 目录里最新的那个文件，而不是导出记录表：
/// 服务管理台本机导出的包不经过 API，只看表会把它们全漏掉，
/// 于是「明明昨天刚导过」却报超期。
/// </summary>
public class ConfigBackupStatusDto
{
    /// <summary>配置备份包目录（绝对路径），供现场按图索骥。</summary>
    public string Directory { get; set; } = null!;

    /// <summary>最近一份配置备份包的文件名；从未导出过时为空。</summary>
    public string? LatestFileName { get; set; }

    /// <summary>最近一份的生成时刻（UTC）。</summary>
    public DateTime? LatestExportedAt { get; set; }

    public long? LatestSizeBytes { get; set; }

    /// <summary>距今天数（向下取整）；从未导出过时为空。</summary>
    public int? AgeDays { get; set; }

    /// <summary>目录里的包总数。</summary>
    public int PackageCount { get; set; }

    /// <summary>超期阈值（天）。超过它会产生 config_backup_overdue 告警。</summary>
    public int OverdueDays { get; set; }

    /// <summary>当前是否已超期（从未导出过也算超期）。</summary>
    public bool Overdue { get; set; }

    /// <summary>最近一次**通过管理网页**发起的导出结果；本机导出不在此列。</summary>
    public ConfigBackupExportDto? LastRemoteExport { get; set; }
}

/// <summary>一次通过管理网页发起的导出记录。</summary>
public class ConfigBackupExportDto
{
    public Guid Id { get; set; }

    /// <summary>running / succeeded / failed</summary>
    public string Status { get; set; } = null!;

    public string? FileName { get; set; }

    public long? SizeBytes { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? RequestedByName { get; set; }
}

/// <summary>
/// 配置备份包下载令牌（与恢复下载同一套口径：库里只存哈希，明文只在本响应里出现一次）。
/// 包内有 CA 私钥，是全系统最敏感的文件，因此令牌一次性、有有效期。
/// </summary>
public class ConfigBackupDownloadTokenDto
{
    public string FileName { get; set; } = null!;

    public long SizeBytes { get; set; }

    public string DownloadToken { get; set; } = null!;

    /// <summary>下载地址（相对路径，前端拼接服务基址）</summary>
    public string DownloadUrl { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }
}
