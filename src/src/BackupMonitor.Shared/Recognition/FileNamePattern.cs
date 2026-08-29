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

        // 分卷归档先走专门的形态：按「最后一个点」切的话，db.7z.001 的扩展名会被当成
        // .001，主干 db.7z 里没有 ≥3 位数字段可归一化，于是三卷变成三个互不相同的模式。
        // 三个模式各出现一次，「个数一致」判成立、PresentIn 却只有 1/3——
        // 这个结构在候选列表里会碎成一地，谁都不推荐，缺卷更无从谈起。
        var volumePattern = VolumePattern(fileName);
        if (volumePattern is not null)
            return volumePattern;

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
    public static string Describe(IEnumerable<string> patterns) => string.Join('、', patterns);

    // ---------- 分卷归档 ----------

    /// <summary>`db.7z.001` / `data.zip.002`：主扩展名之后再跟一个三位卷号。</summary>
    private static readonly Regex NumberedVolume = new(
        @"^(?<stem>.+\.[A-Za-z0-9]{1,5})\.(?<index>\d{2,3})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>`db.z01` / `db.r01`：WinZip / WinRAR 的分卷扩展名；末卷是 `.zip` / `.rar`。</summary>
    private static readonly Regex LetteredVolume = new(
        @"^(?<stem>.+)\.(?<kind>z|r)(?<index>\d{2,3})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>`db.part1.rar` / `db.part01.rar`。</summary>
    private static readonly Regex PartVolume = new(
        @"^(?<stem>.+)\.part(?<index>\d{1,3})\.(?<ext>[A-Za-z0-9]{1,5})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>
    /// 分卷归档的一卷：主干名 + 卷号。不是分卷则返回 null。
    ///
    /// 存在的理由：分卷数**天然随数据量浮动**，而 InferRequiredFiles 第二档的判据是
    /// 「各单元里的个数一致」。那条判据对 U8 的 UFFile 附件库是对的（个数随启用年度不同，
    /// 拿它当判据只会制造假警报），但对分卷用反了——`db_*.7z` 永远个数不一致、
    /// 永远不被推荐、于是**缺一卷整份无法恢复却不告警**。
    ///
    /// 分卷该用的尺子是**连续性**：卷号从 1 连续到 max，中间不缺号。
    /// </summary>
    public static (string Stem, int Index)? ParseVolume(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var part = PartVolume.Match(fileName);
        if (part.Success && int.TryParse(part.Groups["index"].Value, out var partIndex))
            return ($"{part.Groups["stem"].Value}.{part.Groups["ext"].Value}", partIndex);

        var numbered = NumberedVolume.Match(fileName);
        if (numbered.Success && int.TryParse(numbered.Groups["index"].Value, out var numberedIndex))
            return (numbered.Groups["stem"].Value, numberedIndex);

        var lettered = LetteredVolume.Match(fileName);
        if (lettered.Success && int.TryParse(lettered.Groups["index"].Value, out var letteredIndex))
        {
            // .z01 的末卷是 .zip、.r01 的末卷是 .rar：主干统一成末卷的名字，
            // 这样 `db.z01`、`db.z02`、`db.zip` 会归到同一个主干下。
            var suffix = lettered.Groups["kind"].Value.Equals("z", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".rar";
            return ($"{lettered.Groups["stem"].Value}{suffix}", letteredIndex);
        }

        return null;
    }

    /// <summary>
    /// 分卷文件的归一化模式：`db.7z.001` → `db.7z.*`、`db.z01` → `db.z*`、
    /// `db.part1.rar` → `db.part*.rar`。不是分卷返回 null。
    ///
    /// 卷号是可变段，主干才是这一组的身份——这与 Normalize 对日期、长数字段的处理是同一条尺度。
    /// </summary>
    public static string? VolumePattern(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var part = PartVolume.Match(fileName);
        if (part.Success)
            return $"{part.Groups["stem"].Value}.part*.{part.Groups["ext"].Value}";

        var numbered = NumberedVolume.Match(fileName);
        if (numbered.Success)
            return $"{numbered.Groups["stem"].Value}.*";

        var lettered = LetteredVolume.Match(fileName);
        return lettered.Success
            ? $"{lettered.Groups["stem"].Value}.{lettered.Groups["kind"].Value}*"
            : null;
    }

    /// <summary>
    /// 分卷序列的末卷（`db.zip` 之于 `db.z01`、`db.rar` 之于 `db.r01`）也算这一卷序列的一员，
    /// 它的卷号按「最大卷号 + 1」算。返回它所属的主干名，不是末卷则返回 null。
    /// </summary>
    public static string? VolumeTailStem(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        var lower = fileName.ToLowerInvariant();
        return lower.EndsWith(".zip") || lower.EndsWith(".rar") ? fileName : null;
    }

    // ---------- 复合扩展名 ----------

    /// <summary>
    /// 整体识别的复合扩展名。
    ///
    /// 只取最后一个点的话，`backup.tar.gz` 的扩展名是 `.gz`、主干是 `backup.tar`，
    /// 于是成对检测（按主干分组）会把 `backup.tar.gz` 和 `backup.tar.bz2` 分到两组，
    /// 而扩展名分组又把所有 `.gz` 混成一类——两处都串。
    ///
    /// 白名单而不是「凡是两段都合并」：`2026-08-30@02_00.zip` 的主干里有点，
    /// 无条件合并会把 `@02_00.zip` 当成扩展名。
    /// </summary>
    private static readonly string[] CompoundExtensions =
    [
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".sql.gz", ".sql.bz2", ".sql.xz"
    ];

    /// <summary>文件扩展名（含点），复合扩展名整体返回。没有扩展名返回空串。</summary>
    public static string Extension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        foreach (var compound in CompoundExtensions)
        {
            if (fileName.EndsWith(compound, StringComparison.OrdinalIgnoreCase)
                && fileName.Length > compound.Length)
            {
                return fileName[^compound.Length..];
            }
        }

        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? string.Empty : fileName[dot..];
    }

    /// <summary>去掉扩展名之后的主干名，复合扩展名整体去掉：`backup.tar.gz` → `backup`。</summary>
    public static string BaseName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;
        var extension = Extension(fileName);
        return extension.Length == 0 ? fileName : fileName[..^extension.Length];
    }
}
