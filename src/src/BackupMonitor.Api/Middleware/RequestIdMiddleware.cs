using Serilog.Context;

namespace BackupMonitor.Api.Middleware;

/// <summary>请求追踪 ID 中间件（设计书 8.2：每个响应携带 requestId）</summary>
public class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-Id";
    public const string ContextItemKey = "RequestId";

    private readonly RequestDelegate _next;

    public RequestIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestId))
            requestId = Guid.NewGuid().ToString("N");

        context.Items[ContextItemKey] = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("RequestId", requestId))
        {
            await _next(context);
        }
    }
}
