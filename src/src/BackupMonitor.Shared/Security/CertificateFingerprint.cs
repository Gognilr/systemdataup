namespace BackupMonitor.Shared.Security;

/// <summary>
/// TLS 证书指纹的规范化与展示格式。
///
/// 局域网形态下服务端用的是自签名证书，没有公共 CA 可以背书，客户端首次连接时
/// 「对面这张证书到底是不是真服务端的」这一判断计算机做不了——响应体里的指纹和
/// 实际出示的证书可以由同一个中间人同时伪造，两者自洽并不能证明什么。
/// 唯一的解法是操作员带外核对：服务端安装器显示一次，客户端安装器显示一次，人来比。
///
/// 既然要人眼比，两端的显示格式就必须一模一样，否则比对本身会变成负担并诱发误判。
/// 因此格式化只有这一份实现，两个安装器共用。
/// </summary>
public static class CertificateFingerprint
{
    /// <summary>分组宽度。4 位一组是人眼逐段核对时最不容易串行的粒度。</summary>
    private const int GroupSize = 4;

    /// <summary>每行组数。SHA-256 是 64 个十六进制字符，正好 4 行 × 4 组。</summary>
    private const int GroupsPerLine = 4;

    /// <summary>
    /// 规范化：去掉冒号、空格、连字符等一切分隔符并转大写。
    /// 用于比较——比较必须在规范化后的值上做，不能比字面量。
    /// </summary>
    public static string Normalize(string? value) =>
        new string((value ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray())
        .ToUpperInvariant();

    /// <summary>
    /// 展示格式：4 位一组、4 组一行。仅用于显示给人看，不要拿它去比较。
    /// </summary>
    public static string ToDisplayBlock(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0)
            return string.Empty;

        var groups = new List<string>();
        for (var offset = 0; offset < normalized.Length; offset += GroupSize)
            groups.Add(normalized.Substring(offset, Math.Min(GroupSize, normalized.Length - offset)));

        var lines = new List<string>();
        for (var index = 0; index < groups.Count; index += GroupsPerLine)
            lines.Add(string.Join("  ", groups.Skip(index).Take(GroupsPerLine)));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 单行展示格式：4 位一组，空格分隔。给状态行、日志行这类只有一行位置的地方用；
    /// 需要多行分块的用 <see cref="ToDisplayBlock"/>。同样仅用于显示，不要拿它去比较。
    /// </summary>
    public static string ToGroupedHex(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0)
            return string.Empty;

        var groups = new List<string>();
        for (var offset = 0; offset < normalized.Length; offset += GroupSize)
            groups.Add(normalized.Substring(offset, Math.Min(GroupSize, normalized.Length - offset)));
        return string.Join(' ', groups);
    }

    /// <summary>两个指纹是否为同一张证书。始终经规范化后比较。</summary>
    public static bool Matches(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        return normalizedLeft.Length > 0
            && string.Equals(normalizedLeft, Normalize(right), StringComparison.Ordinal);
    }
}
