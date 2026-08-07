using System.Reflection;
using BackupMonitor.Infrastructure.Data.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupMonitor.Infrastructure.Data.Extensions;

/// <summary>ModelBuilder 扩展方法</summary>
public static class ModelBuilderExtensions
{
    private static readonly MethodInfo ApplyConverterMethod =
        typeof(ModelBuilderExtensions).GetMethod(
            nameof(ApplyConverter), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// 扫描模型中所有枚举属性（含可空枚举），批量应用 SnakeCaseEnumConverter，
    /// 并统一列类型为 varchar(32)（与 PostgreSQL DOMAIN 基础类型一致）。
    /// </summary>
    public static void ApplySnakeCaseEnumConverters(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var underlyingType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (!underlyingType.IsEnum)
                    continue;

                ApplyConverterMethod
                    .MakeGenericMethod(entityType.ClrType, underlyingType)
                    .Invoke(null, [modelBuilder, property.Name]);
            }
        }
    }

    private static void ApplyConverter<TEntity, TEnum>(ModelBuilder modelBuilder, string propertyName)
        where TEntity : class
        where TEnum : struct, Enum
    {
        modelBuilder.Entity<TEntity>()
            .Property(propertyName)
            .HasConversion(new SnakeCaseEnumConverter<TEnum>())
            .HasColumnType("varchar(32)");
    }
}
