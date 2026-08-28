using System.Text;
using System.Text.RegularExpressions;

namespace BackupMonitor.Shared.Recognition;

/// <summary>
/// 文件名归一化：把「名字每次不同、模式每次相同」的那一类文件收敛成一个可比对的模式。
///
/// 为什么需要这个原语：识别器此前只有两档判断——**精确比文件名**，或者退一步**比扩展名**。
/// 中间那一档不存在，于是两类非常常见的备份结构都认不出来：
///
///   用友 U8 的附件库    UFFile_001_2022.dat … UFFile_001_2026.dat
///   每日转储的数据库    dzwl_product_backup_2026_08_28_000002_2768823.bak
///
/// 精确名统计里它们永远凑不够出现次数，直接从候选里消失；按扩展名统计又太粗，
/// 两个不同的库都是 *.bak，分不出「今天少备了一个库」。归一化后
/// （UFFile_*.dat、dzwl_product_backup_*.bak）才既稳定又有区分度。
///
/// 归一化的尺度是刻意选的：**替换日期形态，以及长度 ≥3 的数字段**。
/// 1–2 位数字通常是有意义的区分位（_1、_02），3 位以上基本是序号、年份、时间戳。
/// 这个尺度会把 UFFile_001_2022.dat 和 UFFile_002_2024.dat 收敛成同一个模式——
/// 那正是想要的：跨账套比对时，账套号本身不该成为区分依据。
/// </summary>
public static class FileNamePattern
{
    /// <summary>归一化过程中代表「被抹掉的一段」的占位符。挑一个文件名里不可能出现的控制字符。</summary>
    private const char Placeholder = '\x01';

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>2026-08-22 / 2026_08_22 / 2026.08.22</summary>
    private static readonly Regex SeparatedDate = new(
        @"(?<!\d)(19|20)\d{2}(?<sep>[-_.])(0[1-9]|1[0-2])\k<sep>(0[1-9]|[12]\d|3[01])(?!\d)",
        RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>20260822</summary>
    private static readonly Regex CompactDate = new(
        @"(?<!\d)(19|20)\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])(?!\d)",
        RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>剩下的长数字段：序号、时间戳、备份号。</summary>
    private static readonly Regex LongDigits = new(@"\d{3,}", RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>把相邻的两个占位符（中间只隔一个分隔符）并成一个。</summary>
    private static readonly Regex AdjacentPlaceholders = new(
        $@"{Placeholder}([-_.@ ]{Placeholder})+", RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>
    /// 文件名 → 通配模式。扩展名原样保留，只归一化主干部分。
    ///
    /// <code>
    /// UFFile_001_2022.dat                                → UFFile_*.dat
    /// UFDATA.BAK                                         → UFDATA.BAK
    /// dzwl_product_backup_2026_08_28_000002_2768823.bak  → dzwl_product_backup_*.bak
    /// db_20260823.bak                                    → db_*.bak
    /// </code>
    /// </summary>
    public static string Normalize(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return fileName;

        var dot = fileName.LastIndexOf('.');
        // 前导点的文件（.gitignore）不算有扩展名——与 RecognizerRules 的边界保持一致。
        var stem = dot <= 0 ? fileName : fileName[..dot];
        var extension = dot <= 0 ? string.Empty : fileName[dot..];

        var replaced = SeparatedDate.Replace(stem, Placeholder.ToString());
        replaced = CompactDate.Replace(replaced, Placeholder.ToString());
        replaced = LongDigits.Replace(replaced, Placeholder.ToString());
        replaced = AdjacentPlaceholders.Replace(replaced, Placeholder.ToString());

        // 主干被抹光时收尾的分隔符留着没有意义：db_ → db，*_ → *。
        var trimmed = replaced.Trim('-', '_', '.', '@', ' ');
        if (trimmed.Length == 0)
            trimmed = Placeholder.ToString();

        return trimmed.Replace(Placeholder, '*') + extension;
    }

    /// <summary>这个文件名归一化之后是否真的被改动过（即它含有可变片段）。</summary>
    public static bool HasVariablePart(string fileName) =>
        !string.Equals(Normalize(fileName), fileName, StringComparison.Ordinal);

    /// <summary>
    /// 从文件名里取出日期片段（`2026_08_22` / `2026-08-22` / `20260822`），取不到返回 null。
    /// 用来把「一个平铺目录里堆着好几天、每天好几个库」按天分组。
    /// </summary>
    public static string? ExtractDateToken(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var separated = SeparatedDate.Match(fileName);
        if (separated.Success)
            return separated.Value;

        var compact = CompactDate.Match(fileName);
        return compact.Success ? compact.Value : null;
    }

    /// <summary>
    /// 为「按文件名里的日期分组」生成一条 groupBy 正则（带一个捕获组）。
    /// 取不到日期片段时返回 null——推断端据此放弃这条规则，而不是写一条匹配不上的正则。
    ///
    /// 匹配不上的后果不是报错而是**静默退化**：RecognizerRules.GroupKeyOf 会把所有文件
    /// 归进同一个「未分组」键并全部保留，等于不分组。所以调用方拿到正则后还必须在样本上
    /// 自检一遍，见 <see cref="Validate"/>。
    /// </summary>
    public static string? BuildDateGroupRegex(string fileName)
    {
        var separated = SeparatedDate.Match(fileName);
        if (separated.Success)
        {
            var separator = separated.Groups["sep"].Value;
            var escaped = separator == "." ? @"\." : separator;
            return $@"(\d{{4}}{escaped}\d{{2}}{escaped}\d{{2}})";
        }

        return CompactDate.IsMatch(fileName) ? @"(\d{8})" : null;
    }

    /// <summary>
    /// 在样本文件名上验证一条 groupBy 正则：必须每个文件都能取到键，且至少分出两组。
    /// 任一条不满足就不该把这条规则写进配置。
    /// </summary>
    public static bool Validate(string groupByRegex, IEnumerable<string> fileNames)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileNames)
        {
            Match match;
            try
            {
                match = Regex.Match(name, groupByRegex,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }

            if (!match.Success || match.Groups.Count < 2 || !match.Groups[1].Success)
                return false;
            keys.Add(match.Groups[1].Value);
        }

        return keys.Count >= 2;
    }

    /// <summary>一组模式的可读描述，用于摘要文案：`beeServer_backup_*.bak`、`dzwl_product_backup_*.bak`。</summary>
    public static string Describe(IEnumerable<string> patterns)
    {
        var builder = new StringBuilder();
        foreach (var pattern in patterns)
        {
            if (builder.Length > 0)
                builder.Append('、');
            builder.Append(pattern);
        }

        return builder.ToString();
    }
}
