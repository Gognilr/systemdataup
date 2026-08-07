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

        // 统一为正斜杠后检查
        var normalized = relativePath.Replace('\\', '/').Trim();

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

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                return false;
            if (segment.EndsWith(' '))
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
