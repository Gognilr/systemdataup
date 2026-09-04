using BackupMonitor.Infrastructure.Services;

namespace BackupMonitor.Api.Middleware;

/// <summary>
/// 把「收到过这个客户端的请求」记成存活证据。
///
/// 位于认证之后（要读 client_id claim）。只对 mTLS 认证过的 Agent 请求生效——
/// 管理端 JWT 的主体不含这个 claim，不受影响。
///
/// 为什么值得单独有一个中间件：存活判定此前唯一的输入是心跳，而心跳只有一个端点在写。
/// 领指令、报扫描进度、传分块这些请求既比心跳频繁、又比心跳更能说明客户端在干活，
/// 却一条都不算数。理由与限流细节见 <see cref="ClientLastSeenTracker"/>。
///
/// 不阻塞请求：时间戳写不进去也照常放行，这是旁路信息，不是这次请求要办的事。
/// </summary>
public sealed class ClientLastSeenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ClientLastSeenTracker _tracker;

    public ClientLastSeenMiddleware(RequestDelegate next, ClientLastSeenTracker tracker)
    {
        _next = next;
        _tracker = tracker;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst("client_id")?.Value, out var clientId))
        {
            await _tracker.TouchAsync(clientId, context.RequestAborted);
        }

        await _next(context);
    }
}
