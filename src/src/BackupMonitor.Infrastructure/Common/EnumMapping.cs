using System.Text.Json;
using BackupMonitor.Shared.Exceptions;

namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// snake_case 字符串与枚举互转（API 契约使用 snake_case，实体使用枚举）。
/// </summary>
public static class EnumMapping
{
    private static readonly JsonNamingPolicy SnakeCase = JsonNamingPolicy.SnakeCaseLower;

    /// <summary>枚举值 → snake_case 字符串</summary>
    public static string ToSnakeCase<TEnum>(TEnum value) where TEnum : struct, Enum =>
        SnakeCase.ConvertName(value.ToString());

    /// <summary>snake_case 字符串 → 枚举值（失败抛 INVALID_REQUEST）</summary>
    public static TEnum ParseSnakeCase<TEnum>(string? value, string fieldName) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new BusinessException("INVALID_REQUEST", $"缺少必填字段 {fieldName}", 400);

        if (TryParseSnakeCase<TEnum>(value, out var parsed))
            return parsed;

        var valid = string.Join(", ", Enum.GetValues<TEnum>().Select(ToSnakeCase));
        throw new BusinessException(
            "INVALID_REQUEST",
            $"字段 {fieldName} 的值 '{value}' 无效，允许值：{valid}",
            400);
    }

    /// <summary>snake_case 字符串 → 枚举值（可空版本，空输入返回 null）</summary>
    public static TEnum? ParseSnakeCaseOrNull<TEnum>(string? value, string fieldName) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return ParseSnakeCase<TEnum>(value, fieldName);
    }

    /// <summary>尝试解析，不抛异常</summary>
    public static bool TryParseSnakeCase<TEnum>(string value, out TEnum result) where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(SnakeCase.ConvertName(candidate.ToString()), value, StringComparison.OrdinalIgnoreCase))
            {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }
}
