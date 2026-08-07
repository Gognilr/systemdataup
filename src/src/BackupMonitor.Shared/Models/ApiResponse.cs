namespace BackupMonitor.Shared.Models;

/// <summary>统一错误对象（设计书 8.3 失败响应中的 error 节点）</summary>
public class ApiError
{
    /// <summary>错误码（对应设计书第22章错误码表）</summary>
    public string Code { get; set; } = null!;

    /// <summary>错误描述</summary>
    public string Message { get; set; } = null!;

    /// <summary>附加错误明细（可选，如字段级错误、资源标识）</summary>
    public object? Details { get; set; }
}

/// <summary>
/// 统一 API 响应包装（设计书 8.3）。
/// 成功：{ success, requestId, data, message }
/// 失败：{ success, requestId, error: { code, message, details } }
/// </summary>
public class ApiResponse<T>
{
    /// <summary>是否成功</summary>
    public bool Success { get; set; }

    /// <summary>请求追踪 ID</summary>
    public string? RequestId { get; set; }

    /// <summary>业务数据（仅成功时）</summary>
    public T? Data { get; set; }

    /// <summary>提示信息</summary>
    public string? Message { get; set; }

    /// <summary>错误对象（仅失败时）</summary>
    public ApiError? Error { get; set; }

    public static ApiResponse<T> Ok(T data, string? message = null) =>
        new() { Success = true, Data = data, Message = message };

    public static ApiResponse<T> Fail(string errorCode, string message, object? details = null) =>
        new()
        {
            Success = false,
            Error = new ApiError { Code = errorCode, Message = message, Details = details }
        };
}

/// <summary>无数据体的 API 响应</summary>
public class ApiResponse : ApiResponse<object>
{
    public static ApiResponse Ok(string? message = null) =>
        new() { Success = true, Message = message };

    public static new ApiResponse Fail(string errorCode, string message, object? details = null) =>
        new()
        {
            Success = false,
            Error = new ApiError { Code = errorCode, Message = message, Details = details }
        };
}
