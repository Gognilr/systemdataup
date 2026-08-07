using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupMonitor.Infrastructure.Data.Converters;

/// <summary>
/// C# PascalCase 枚举 ↔ PostgreSQL DOMAIN（varchar snake_case 值）转换器。
/// 例：ClientStatus.PendingApproval ↔ "pending_approval"。
/// </summary>
public class SnakeCaseEnumConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public SnakeCaseEnumConverter()
        : base(
            convertToProviderExpression: v => SnakeCaseHelper.ToSnakeCase(v.ToString()),
            convertFromProviderExpression: v => SnakeCaseHelper.FromSnakeCase<TEnum>(v),
            mappingHints: new ConverterMappingHints(size: 32))
    {
    }
}

/// <summary>snake_case / PascalCase 互转工具</summary>
public static class SnakeCaseHelper
{
    /// <summary>PascalCase → snake_case（如 SuspectedOffline → suspected_offline）</summary>
    public static string ToSnakeCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
            return pascalCase;

        var sb = new StringBuilder(pascalCase.Length + 8);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (i > 0 && char.IsUpper(c))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>snake_case → 枚举值（如 waiting_stable → WaitingStable）</summary>
    public static TEnum FromSnakeCase<TEnum>(string snakeCase)
        where TEnum : struct, Enum
    {
        var parts = snakeCase.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var pascal = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return Enum.Parse<TEnum>(pascal);
    }
}
