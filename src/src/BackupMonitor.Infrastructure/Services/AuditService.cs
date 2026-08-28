using System.Security.Claims;
using BackupMonitor.Core.Entities.Audit;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>当前请求上下文（用户/客户端、IP、RequestId）</summary>
public interface ICurrentContext
{
    Guid? UserId { get; }
    string? Username { get; }
    Guid? ClientId { get; }
    string? ClientIp { get; }
    string? UserAgent { get; }
    string? RequestId { get; }
}

/// <summary>基于 HttpContext 的当前上下文实现</summary>
public class HttpContextCurrentContext : ICurrentContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private HttpContext? Context => _accessor.HttpContext;

    public Guid? UserId =>
        Guid.TryParse(Context?.User?.FindFirst("sub")?.Value, out var id) ? id : null;

    public Guid? ClientId =>
        Guid.TryParse(Context?.User?.FindFirst("client_id")?.Value, out var id) ? id : null;

    public string? Username => Context?.User?.FindFirst("username")?.Value;

    /// <summary>
    /// 服务端看到的对端地址。
    ///
    /// 双栈监听（Kestrel 默认的 IPv6Any）下，IPv4 客户端进来时内核给的是 IPv4 映射地址，
    /// 直接 ToString() 会把 192.168.1.10 存成 ::ffff:192.168.1.10——
    /// 审计里按 IP 查对不上，客户端列表的 IP 列也显示成没人认得出的形状。
    /// 这里把映射地址还原回本来的 IPv4；真正走 IPv6 连上来的原样保留，不做任何猜测。
    /// </summary>
    public string? ClientIp
    {
        get
        {
            var address = Context?.Connection.RemoteIpAddress;
            if (address is null)
                return null;
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
        }
    }

    public string? UserAgent
    {
        get
        {
            var ua = Context?.Request.Headers.UserAgent.ToString();
            return ua is { Length: > 512 } ? ua[..512] : ua;
        }
    }

    public string? RequestId => Context?.Items.TryGetValue("RequestId", out var v) == true ? v?.ToString() : null;
}

/// <summary>审计记录服务（设计书 21/27：关键写操作生成审计日志，只追加）</summary>
public interface IAuditRecorder
{
    /// <summary>记录一条审计日志（不抛出，失败仅记错误日志）</summary>
    Task RecordAsync(
        string action,
        AuditResult result,
        string? resourceType = null,
        Guid? resourceId = null,
        string? beforeData = null,
        string? afterData = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken ct = default);
}

/// <summary>审计记录实现</summary>
public class DbAuditRecorder : IAuditRecorder
{
    private readonly AppDbContext _db;
    private readonly ICurrentContext _context;
    private readonly ILogger<DbAuditRecorder> _logger;

    public DbAuditRecorder(AppDbContext db, ICurrentContext context, ILogger<DbAuditRecorder> logger)
    {
        _db = db;
        _context = context;
        _logger = logger;
    }

    public async Task RecordAsync(
        string action,
        AuditResult result,
        string? resourceType = null,
        Guid? resourceId = null,
        string? beforeData = null,
        string? afterData = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        try
        {
            var log = new AuditLog
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTime.UtcNow,
                UserId = _context.UserId,
                UsernameSnapshot = _context.Username,
                ClientIp = _context.ClientIp,
                Action = action,
                ResourceType = resourceType,
                ResourceId = resourceId,
                RequestId = _context.RequestId,
                Result = result,
                BeforeData = beforeData,
                AfterData = afterData,
                ErrorCode = errorCode,
                ErrorMessage = Truncate(errorMessage, 2000),
                UserAgent = _context.UserAgent
            };

            _db.AuditLogs.Add(log);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // 审计失败不阻断主流程
            _logger.LogError(ex, "写入审计日志失败 action={Action} resource={Resource}", action, resourceType);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
