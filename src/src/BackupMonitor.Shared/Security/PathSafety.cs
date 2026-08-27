namespace BackupMonitor.Shared.Security;

/// <summary>
/// 上传相对路径安全校验（设计书 23.4 路径安全）：
/// 拒绝 ..、绝对路径、UNC、设备路径、非法字符；服务端规范化后再次校验。
/// </summary>
public static class PathSafety
{
    private const int MaxPathLength = 2048;

    /// <summary>
    /// Windows 保留设备名（审查 P2-6）。这些名字在任何目录下都不能作为文件名使用，
    /// 带扩展名同样保留（NUL.txt 依旧指向空设备）。写入会「静默成功」而内容丢失，
    /// 因此必须在入口拒绝，而不是等写盘时才发现。
    /// </summary>
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>判断一个路径段是否命中 Windows 保留设备名（忽略扩展名）</summary>
    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot >= 0 ? segment[..dot] : segment;
        return WindowsReservedNames.Contains(stem);
    }

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
            if (IsReservedDeviceName(segment))
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

    /// <summary>组件长度上限（码位，审计 H-17）</summary>
    private const int MaxComponentLength = 100;

    /// <summary>
    /// 清理目录/文件名中的非法字符（服务端生成正式路径用）。
    ///
    /// 审计 H-17：截断必须发生在收尾净化**之前**。
    /// 原先的顺序是「替非法字符 → 去 .. → Trim → TrimEnd('.') → 最后截断到 100」，
    /// 于是截断点恰好落在点或空格上时这层保护就被绕过了：NTFS 写盘时会静默剥掉
    /// 结尾的点和空格，backup_sets.repository_path 记的路径和磁盘上实际的目录名
    /// 从此不一致。日常读写因为 Win32 会做同样的规范化而看不出问题，
    /// 但任何逐字符比对路径的逻辑都会踩到——PathSafety.IsUnderBase 围栏、
    /// 跨平台迁移、目录审计。IsValidRelativePath 里对客户端提交路径的同一条规则
    /// 本来就是完整的，只有服务端自己生成路径这条漏了。
    ///
    /// 截断按码位而不是 UTF-16 码元：中文在 BMP 内所以现在看不出问题，
    /// 主机名里出现 emoji 或扩展 B 区汉字时按码元截会切断代理对，
    /// 留下半个字符——那是个连文件系统都未必接受的名字。
    /// </summary>
    public static string SanitizePathComponent(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var cleaned = TruncateByRunes(sb.ToString().Replace("..", "_"), MaxComponentLength);

        // 截断之后再跑收尾净化：截断点落在点或空格上时，这一步把它清掉。
        cleaned = cleaned.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "unnamed";

        // 服务端生成的目录名同样不能撞上保留设备名（审查 P2-6）：
        // 主机名或任务名恰好叫 CON/NUL 时，Directory.CreateDirectory 会失败或行为异常。
        if (IsReservedDeviceName(cleaned))
        {
            // 加前缀可能让长度回到 101。保留名都很短，实际撞不上，
            // 但长度上限是这个方法对调用方的承诺，不能靠「不会发生」来兑现。
            cleaned = TruncateByRunes("_" + cleaned, MaxComponentLength).TrimEnd('.', ' ');
        }

        return cleaned;
    }

    /// <summary>按码位截断，绝不切断代理对（审计 H-17）</summary>
    private static string TruncateByRunes(string value, int maxRunes)
    {
        var runes = 0;
        var index = 0;
        while (index < value.Length && runes < maxRunes)
        {
            index += char.IsHighSurrogate(value[index]) && index + 1 < value.Length ? 2 : 1;
            runes++;
        }

        return index >= value.Length ? value : value[..index];
    }
}
