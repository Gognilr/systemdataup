namespace BackupMonitor.Shared.Models;

/// <summary>分页查询参数基类</summary>
public class PagedQuery
{
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    private int _page = 1;
    private int _pageSize = DefaultPageSize;

    /// <summary>页码（从 1 开始）</summary>
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    /// <summary>每页大小（上限 200，对应系统配置 max_page_size）</summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => value
        };
    }

    /// <summary>排序字段</summary>
    public string? SortBy { get; set; }

    /// <summary>是否降序</summary>
    public bool SortDescending { get; set; }

    /// <summary>关键字搜索</summary>
    public string? Keyword { get; set; }
}
