using System.Linq.Expressions;

namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// 列表排序白名单（审查 P1-3）。
///
/// PagedQuery 早就定义了 SortBy / SortDescending，但全仓没有任何服务消费它们：
/// 前端点击表头，箭头会翻转、列表会重新请求，顺序永远不变——一个会误导用户的死控件。
///
/// 这里用「显式白名单 + 强类型排序委托」接通这条链路：
/// 只有登记过的字段名可排序，未登记或为空一律回落到默认排序。
/// 不用反射拼表达式，因此既不可能被注入任意字段名，也不会在 EF 翻译时出现装箱问题。
/// </summary>
public static class SortWhitelist
{
    /// <summary>把一个属性选择器包成「按方向排序」的委托，供白名单登记使用。</summary>
    public static Func<IQueryable<T>, bool, IQueryable<T>> By<T, TKey>(Expression<Func<T, TKey>> selector)
        => (query, descending) => descending ? query.OrderByDescending(selector) : query.OrderBy(selector);

    /// <summary>
    /// 按白名单应用排序。sortBy 为空或不在白名单内时使用 fallback，
    /// 保证任何输入都能得到一个确定的顺序（分页依赖稳定排序）。
    /// </summary>
    public static IQueryable<T> ApplySort<T>(
        this IQueryable<T> query,
        string? sortBy,
        bool descending,
        IReadOnlyDictionary<string, Func<IQueryable<T>, bool, IQueryable<T>>> allowed,
        Func<IQueryable<T>, bool, IQueryable<T>> fallback,
        bool fallbackDescending = true)
    {
        if (!string.IsNullOrWhiteSpace(sortBy) && allowed.TryGetValue(sortBy.Trim(), out var sort))
            return sort(query, descending);

        return fallback(query, fallbackDescending);
    }
}
