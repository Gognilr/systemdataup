namespace BackupMonitor.Shared.Models;

/// <summary>分页结果</summary>
public class PagedResult<T>
{
    /// <summary>当前页数据</summary>
    public List<T> Items { get; set; } = [];

    /// <summary>总记录数</summary>
    public long TotalCount { get; set; }

    /// <summary>当前页码（从 1 开始）</summary>
    public int Page { get; set; }

    /// <summary>每页大小</summary>
    public int PageSize { get; set; }

    /// <summary>总页数</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;

    public static PagedResult<T> Create(List<T> items, long totalCount, int page, int pageSize) =>
        new() { Items = items, TotalCount = totalCount, Page = page, PageSize = pageSize };
}
