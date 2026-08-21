namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// 上传相对路径安全校验（设计书 23.4 路径安全）：
/// 拒绝 ..、绝对路径、UNC、设备路径、非法字符；服务端规范化后再次校验。
/// </summary>
public static class PathSafety
{
    private const int MaxPathLength = 2048;

    /// <summary>校验客户端提交的相对路径，非法时返回 false</summary>
    public static bool IsValidRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        if (relativePath.Length > MaxPathLength)
            return false;

        // 统一为正斜杠；段级检查基于未去首尾空白的版本，
        // 否则整条路径末尾的尾随空格会被 Trim 吃掉而漏检。
        // NTFS 写盘时会静默剥离文件名尾随空格与点，此类路径必须拒绝（23.4）。
        var replaced = relativePath.Replace('\\', '/');
        var normalized = replaced.Trim();

        if (normalized.StartsWith('/') || normalized.StartsWith(".."))
            return false;
        if (normalized.Contains("//"))
            return false;

        // UNC / Windows 绝对路径 / 设备路径
        if (relativePath.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        if (relativePath.Length >= 2 && relativePath[1] == ':')
            return false;
        if (relativePath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            relativePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return false;

        foreach (var segment in replaced.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                return false;
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                return false;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 将相对路径拼接到基目录下并校验结果仍位于基目录内，
    /// 非法（或穿越）时返回 null。
    /// </summary>
    public static string? ResolveUnderBase(string basePath, string relativePath)
    {
        if (!IsValidRelativePath(relativePath))
            return null;

        try
        {
            var fullBase = Path.GetFullPath(basePath);
            var combined = Path.GetFullPath(Path.Combine(fullBase, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!combined.StartsWith(
                    fullBase.EndsWith(Path.DirectorySeparatorChar) ? fullBase : fullBase + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return null;

            return combined;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 判断一个已经成形的绝对路径是否位于基目录之内（基目录自身不算"之内"）。
    /// 与 ResolveUnderBase 使用同一套包含判定，供两端已是绝对路径的场景使用——
    /// 典型用途是删除前的围栏：待删目录取自数据库，必须确认它确实落在当前仓库根之下。
    /// </summary>
    public static bool IsUnderBase(string basePath, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(fullPath))
            return false;

        try
        {
            var resolvedBase = Path.GetFullPath(basePath);
            var resolvedTarget = Path.GetFullPath(fullPath);

            var prefix = resolvedBase.EndsWith(Path.DirectorySeparatorChar)
                ? resolvedBase
                : resolvedBase + Path.DirectorySeparatorChar;

            return resolvedTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清理目录/文件名中的非法字符（服务端生成正式路径用）</summary>
    public static string SanitizePathComponent(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var cleaned = sb.ToString().Replace("..", "_").Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "unnamed";
        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }
}
