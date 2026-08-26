namespace BackupMonitor.Shared.Models.Admin;

/// <summary>
/// 存储设置：备份最终存放在哪、上传过程中的暂存放在哪。
/// 这两个路径此前只能在安装时选、或者直接改库，管理界面上没有任何入口。
/// </summary>
public class StorageSettingsDto
{
    /// <summary>正式仓库根（校验通过的备份最终落在这里）</summary>
    public StoragePathDto Repository { get; set; } = new();

    /// <summary>上传暂存根（分块上传的落盘处，提交后清理）</summary>
    public StoragePathDto Staging { get; set; } = new();

    /// <summary>
    /// 仓库路径改过之后，落在当前仓库根之外的备份集数量。
    /// 这些备份集的文件仍然在老目录里可用，但保留策略的物理清理会拒绝删除它们
    /// （RetentionCleanupWorker 的围栏只认当前仓库根），需要人工搬迁或清理。
    /// </summary>
    public int BackupSetsOutsideRoot { get; set; }
}

/// <summary>单个存储根的当前状态</summary>
public class StoragePathDto
{
    /// <summary>管理员显式配置的值；null 表示没配，走回退链</summary>
    public string? ConfiguredPath { get; set; }

    /// <summary>实际生效的路径</summary>
    public string EffectivePath { get; set; } = null!;

    /// <summary>生效值的来源：database / configuration / default</summary>
    public string Source { get; set; } = null!;

    /// <summary>目录当前是否存在</summary>
    public bool Exists { get; set; }

    /// <summary>是否可写（实际写一个探针文件验证，不看 ACL 推断）</summary>
    public bool Writable { get; set; }

    /// <summary>所在磁盘剩余空间（字节）；取不到时为 null</summary>
    public long? FreeBytes { get; set; }

    /// <summary>所在磁盘总容量（字节）；取不到时为 null</summary>
    public long? TotalBytes { get; set; }

    /// <summary>探测过程中的问题描述（目录不可写、盘符不存在等），正常时为 null</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// 更新存储设置。两个字段都留空（null 或空串）表示清除该项配置，
/// 回落到 appsettings 的 Storage:* 或程序目录下的 data 子目录。
/// </summary>
public class UpdateStorageSettingsRequest
{
    public string? RepositoryPath { get; set; }

    public string? StagingPath { get; set; }
}
