namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// 口令策略（OPEN-ISSUES #2）：长度 ≥ 12、不等于用户名、不属于常见弱口令。
///
/// originally 内联在 AuthService.ChangePasswordAsync 里。管理端新增「重置用户口令」
/// 之后必须与自助改密走同一套规则——两份各自演进的口令规则本身就是缺陷，
/// 因此抽到这里由两端共用。
/// </summary>
public static class PasswordPolicy
{
    /// <summary>最短长度</summary>
    public const int MinLength = 12;

    /// <summary>bcrypt 成本，与 V001/V005 的 gen_salt('bf',12) 一致</summary>
    public const int BcryptWorkFactor = 12;

    /// <summary>
    /// 常见弱口令样本（审查 P2-5）。
    ///
    /// 必须说清它的定位：真实弱口令字典是百万量级，这里的十余条**不构成弱口令防线**，
    /// 只拦截最典型的几种「凑够 12 位」写法。把它当成完整防护会产生虚假的安全感——
    /// 这正是原报告点名的问题。真正起作用的是下面三条结构性规则：
    /// 长度下限、不等于用户名、不是单一字符或连续键盘序列重复。
    ///
    /// 若要真正的字典防护，应接入 HaveIBeenPwned k-Anonymity 接口或本地 rockyou 字典，
    /// 那是独立的工程决策（离线环境下需要随包分发字典文件），不在本次修复范围内。
    /// </summary>
    private static readonly HashSet<string> CommonWeakPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password123456", "123456789012", "abcdefghijkl", "qwertyuiop12",
        "admin@2026!!!!", "administrator1", "changeme1234", "welcome12345",
        "aa123456789012", "111111111111", "000000000000",
        "passwordpassword", "qwertyqwerty", "1234567890ab", "adminadmin123"
    };

    /// <summary>
    /// 结构性弱口令判定：这比枚举字典更有效，因为它不依赖穷举。
    /// 覆盖「同一字符重复」「纯数字」「键盘序列」三类——凑长度时最常见的三种写法。
    /// </summary>
    private static bool IsStructurallyWeak(string password)
    {
        var lower = password.ToLowerInvariant();

        // 单一字符重复：aaaaaaaaaaaa
        if (lower.Distinct().Count() <= 2)
            return true;

        // 纯数字：无论多长，字典攻击成本都很低
        if (lower.All(char.IsAsciiDigit))
            return true;

        // 键盘序列 / 字母表序列的连续片段（正序或逆序，长度 ≥ 8）
        const string sequences = "abcdefghijklmnopqrstuvwxyz0123456789qwertyuiopasdfghjklzxcvbnm";
        for (var length = 8; length <= lower.Length; length++)
        {
            for (var start = 0; start + length <= lower.Length; start++)
            {
                var slice = lower.Substring(start, length);
                var reversed = new string(slice.Reverse().ToArray());
                if (sequences.Contains(slice, StringComparison.Ordinal) ||
                    sequences.Contains(reversed, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>校验口令强度，返回错误信息；null 表示通过。</summary>
    public static string? Validate(string? password, string username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"新口令长度不得少于 {MinLength} 位";

        if (string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
            return "新口令不能与用户名相同";

        if (CommonWeakPasswords.Contains(password.ToLowerInvariant()))
            return "新口令属于常见弱口令，请更换";

        if (IsStructurallyWeak(password))
            return "新口令过于简单（重复字符、纯数字或键盘连续序列），请更换";

        return null;
    }

    /// <summary>按统一成本生成 bcrypt 哈希</summary>
    public static string Hash(string password)
        => BCrypt.Net.BCrypt.HashPassword(password, workFactor: BcryptWorkFactor);
}
