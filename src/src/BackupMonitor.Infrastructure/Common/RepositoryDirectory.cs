using BackupMonitor.Shared.Security;

namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// 备份集仓库目录的物理删除，连同它的三道守卫。
///
/// 这段逻辑原本只在 RetentionCleanupWorker 里。V028 之后管理端也能「立即彻底删除」
/// 回收站里的备份集，两处执行的是同一件不可逆的事——递归删除一个来自数据库列的路径，
/// 因此守卫必须是同一份代码，而不是抄一遍。
/// </summary>
public static class RepositoryDirectory
{
    /// <summary>
    /// 删除仓库目录。返回 true 表示确实删了一个存在的目录，
    /// false 表示目录本来就不在（幂等重试、手工清理过）。
    /// 守卫不通过时抛 InvalidOperationException——拒绝并交由人工确认，绝不猜测性地删除。
    /// </summary>
    public static bool DeleteUnderRoot(string? repositoryPath, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            return false;

        var full = Path.GetFullPath(repositoryPath);

        // 守卫一：必须是绝对路径
        if (!Path.IsPathRooted(full))
            throw new InvalidOperationException($"仓库路径不是绝对路径：{full}");

        // 守卫二：不能是盘符根，至少两级目录
        var root = Path.GetPathRoot(full);
        var relative = Path.GetRelativePath(root ?? full, full);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new InvalidOperationException($"仓库路径层级过浅，拒绝删除：{full}");

        // 守卫三（最关键的一道）：待删目录必须落在当前仓库根之内。
        // backup_sets.repository_path 是数据库列，迁移出错、手工改库、仓库路径重配或上游拼接
        // bug 都可能让它指向任意目录，而这里执行的是递归删除——上面的「至少两级」守卫拦不住
        // C:\Program Files\PostgreSQL 这类路径。入库侧（UploadCommitWorker）本来就用
        // PathSafety.ResolveUnderBase 做了基目录约束，销毁侧必须对称。
        //
        // 仓库根被改过之后，历史备份集会落在围栏之外并因此无法物理删除：这是刻意选择的
        // 失败方向——拒绝并告警，交由人工确认。
        if (!PathSafety.IsUnderBase(repositoryRoot, full))
            throw new InvalidOperationException(
                $"仓库目录不在当前仓库根 {Path.GetFullPath(repositoryRoot)} 之内，拒绝删除：{full}");

        if (!Directory.Exists(full))
            return false;

        Directory.Delete(full, recursive: true);
        return true;
    }
}
