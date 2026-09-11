namespace BackupMonitor.Shared.Security;

/// <summary>
/// 配置备份包（.bmbp）的固定落点与命名口径。
///
/// 放在 Shared 而不是安装器工程里，是因为现在有两个入口会碰它：
/// 服务管理台的导出对话框（安装器工程，net8.0-windows）和管理网页的远程导出（API 工程，net8.0）。
/// API 不能引用安装器工程（框架不同，而且安装器反过来引用 API），
/// 两边各写一份常量的结果一定是某天目录名或文件名对不上——
/// 于是「远程看到的上次导出时间」和「本机导出的那一份」变成两笔账。
///
/// 落点定在数据目录旁的固定子目录：这是 2026-09-10 定板的口径。
/// 已知残留风险：同一台机器、很可能同一块盘，系统盘挂掉时配置备份包与它保护的数据一起没。
/// 真正的异地副本依赖管理员自己把 .bmbp 拷走。
/// </summary>
public static class ConfigBackupPaths
{
    /// <summary>数据目录下存放配置备份包的固定子目录名。</summary>
    public const string DirectoryName = "config-backups";

    /// <summary>配置备份包扩展名。</summary>
    public const string Extension = ".bmbp";

    /// <summary>用于枚举目录内备份包的通配符。</summary>
    public const string SearchPattern = "*" + Extension;

    /// <summary>数据目录对应的配置备份包目录（不创建）。</summary>
    public static string DirectoryFor(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), DirectoryName);

    /// <summary>
    /// 默认文件名。用**本地时间**而不是 UTC：这个名字是给站在服务器前面的人看的，
    /// 他要拿它和自己刚才点导出的时刻对上。库里记的时间仍然一律 UTC。
    /// </summary>
    public static string BuildFileName(DateTime localNow) =>
        $"BackupMonitor-backup-{localNow:yyyyMMdd-HHmmss}{Extension}";
}
