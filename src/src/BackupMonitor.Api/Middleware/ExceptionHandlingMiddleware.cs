using System.Text.Json;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Api.Middleware;

/// <summary>全局异常与统一错误响应中间件（设计书 8.3 失败响应形状 / 22 错误码）</summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (BusinessException ex)
        {
            await WriteErrorAsync(context, ex.StatusCode, ex.ErrorCode, ex.Message,
                ex is ValidationFailedException { Errors.Count: > 0 } validation ? validation.Errors : null);
            return;
        }
        catch (DbUpdateConcurrencyException)
        {
            await WriteErrorAsync(context, 409, "CONFLICT", "数据已被其他操作修改，请刷新后重试");
            return;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return; // 客户端主动断开
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "未处理异常 {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteErrorAsync(context, 500, "INTERNAL_ERROR", "服务器内部错误，请稍后重试");
            return;
        }

        // 认证/授权失败时框架只设置状态码不写响应体（401/403），这里补齐统一错误形状
        if (!context.Response.HasStarted
            && context.Response.ContentLength is null or 0
            && context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
        {
            var (code, message) = context.Response.StatusCode == StatusCodes.Status401Unauthorized
                ? ("UNAUTHORIZED", "未认证或认证已过期")
                : ("FORBIDDEN", "权限不足");
            await WriteErrorAsync(context, context.Response.StatusCode, code, message);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string errorCode, string message, object? details = null)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        var requestId = context.Items.TryGetValue(RequestIdMiddleware.ContextItemKey, out var rid)
            ? rid?.ToString() : null;

        var body = new ApiResponse<object>
        {
            Success = false,
            RequestId = requestId,
            Error = new ApiError { Code = errorCode, Message = message, Details = details }
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}
