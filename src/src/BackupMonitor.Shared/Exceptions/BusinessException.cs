namespace BackupMonitor.Shared.Exceptions;

/// <summary>业务异常基类（由全局异常中间件转换为统一错误响应）</summary>
public class BusinessException : Exception
{
    /// <summary>错误码（对应设计书第22章错误码表）</summary>
    public string ErrorCode { get; }

    /// <summary>建议的 HTTP 状态码</summary>
    public int StatusCode { get; }

    public BusinessException(string errorCode, string message, int statusCode = 400)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}

/// <summary>资源不存在（404）</summary>
public class NotFoundException : BusinessException
{
    public NotFoundException(string resource, object id)
        : base("NOT_FOUND", $"{resource} (id={id}) 不存在", 404)
    {
    }
}

/// <summary>乐观锁冲突（409）</summary>
public class ConcurrencyConflictException : BusinessException
{
    public ConcurrencyConflictException(string resource)
        : base("CONFLICT", $"{resource} 已被其他操作修改，请刷新后重试", 409)
    {
    }
}

/// <summary>请求参数校验失败（422）</summary>
public class ValidationFailedException : BusinessException
{
    /// <summary>字段级错误明细</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationFailedException(string message)
        : base("VALIDATION_FAILED", message, 422)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
        : base("VALIDATION_FAILED", "请求参数校验失败", 422)
    {
        Errors = errors;
    }
}

/// <summary>权限不足（403）</summary>
public class ForbiddenException : BusinessException
{
    public ForbiddenException(string message = "权限不足")
        : base("FORBIDDEN", message, 403)
    {
    }
}
