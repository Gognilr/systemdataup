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
    /// 服务端主机名。
    /// 管理页面常常不是在服务器本机上打开的，而这里所有路径、目录选择器列出的磁盘，
    /// 指的都是服务端这台机器——界面必须把这台机器是谁写出来，否则很容易当成本机路径。
    /// </summary>
    public string ServerHostname { get; set; } = null!;

    /// <summary>
    /// 仓库路径改过之后，落在当前仓库根之外的备份集数量。
    /// 这些备份集的文件仍然在老目录里可用，但保留策略的物理清理会拒绝删除它们
    /// （RetentionCleanupWorker 的围栏只认当前仓库根），需要人工搬迁或清理。
    /// </summary>
    public int BackupSetsOutsideRoot { get; set; }

    /// <summary>
    /// 全局同时上传的备份数上限（system_settings 的 max_concurrent_uploads_total）。
    /// 数的是上传会话，而一个会话就是一个业务单元——U8 一台机器 18 个账套时，
    /// 这 18 份是各算各的。它约束的是服务端暂存盘同时被几路读写，所以放在存储设置里。
    /// </summary>
    public int MaxConcurrentUploadsTotal { get; set; }

    /// <summary>
    /// 此刻真的在传的会话数。上限该设几，只看一个孤零零的数字是判断不了的——
    /// 要跟当前实际并发摆在一起，才知道是「一直顶着上限」还是「从来没到过」。
    /// </summary>
    public int ActiveUploads { get; set; }
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
/// 服务端本机目录浏览结果（存储设置的目录选择器用）。
/// 只列目录，不列文件，也不读取任何文件内容。
/// </summary>
public class StorageBrowseResponseDto
{
    /// <summary>true 表示这一层是磁盘列表（未指定 path 时）</summary>
    public bool IsDriveList { get; set; }

    /// <summary>当前所在目录；磁盘列表时为 null</summary>
    public string? Path { get; set; }

    /// <summary>上级目录；已在盘符根或磁盘列表时为 null</summary>
    public string? ParentPath { get; set; }

    public List<StorageBrowseEntryDto> Entries { get; set; } = [];

    /// <summary>子目录太多，只返回了前若干条</summary>
    public bool Truncated { get; set; }

    /// <summary>因权限读不到的子目录数量</summary>
    public int DeniedCount { get; set; }
}

/// <summary>目录浏览的一项（磁盘或子目录）</summary>
public class StorageBrowseEntryDto
{
    public string Name { get; set; } = null!;

    public string Path { get; set; } = null!;

    /// <summary>是否是磁盘根（磁盘列表里为 true）</summary>
    public bool IsDrive { get; set; }

    /// <summary>磁盘剩余空间；仅磁盘项有值</summary>
    public long? FreeBytes { get; set; }

    /// <summary>磁盘总容量；仅磁盘项有值</summary>
    public long? TotalBytes { get; set; }
}

/// <summary>
/// 更新存储设置。两个字段都留空（null 或空串）表示清除该项配置，
/// 回落到 appsettings 的 Storage:* 或程序目录下的 data 子目录。
/// </summary>
public class UpdateStorageSettingsRequest
{
    public string? RepositoryPath { get; set; }

    public string? StagingPath { get; set; }

    /// <summary>
    /// 全局同时上传上限，1–64。null 表示这次不改它——
    /// 路径和并发是同一个表单提交的，漏传一个字段不该把它重置成默认值。
    /// </summary>
    public int? MaxConcurrentUploadsTotal { get; set; }
}
