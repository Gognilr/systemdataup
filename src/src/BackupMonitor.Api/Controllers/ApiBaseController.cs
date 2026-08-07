using System.Security.Claims;
using BackupMonitor.Api.Middleware;
using BackupMonitor.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>控制器基类：统一响应包装与 RequestId 填充</summary>
[ApiController]
public abstract class ApiBaseController : ControllerBase
{
    /// <summary>当前请求追踪 ID（RequestIdMiddleware 写入）</summary>
    protected string RequestId =>
        HttpContext.Items.TryGetValue(RequestIdMiddleware.ContextItemKey, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;

    /// <summary>当前认证的管理员用户 ID（JWT sub）</summary>
    protected Guid? UserId =>
        Guid.TryParse(User.FindFirst("sub")?.Value, out var id) ? id : null;

    /// <summary>当前认证的客户端 ID（mTLS client_id claim）</summary>
    protected Guid ClientIdentity
    {
        get
        {
            var claim = User.FindFirst("client_id")?.Value;
            if (Guid.TryParse(claim, out var id))
                return id;
            throw new InvalidOperationException("当前请求缺少 client_id claim（客户端认证异常）");
        }
    }

    /// <summary>成功响应（带数据）</summary>
    protected ActionResult<ApiResponse<T>> OkData<T>(T data, string? message = null)
    {
        var response = ApiResponse<T>.Ok(data, message);
        response.RequestId = RequestId;
        return Ok(response);
    }

    /// <summary>成功响应（无数据）</summary>
    protected ActionResult<ApiResponse> OkMessage(string? message = null)
    {
        var response = ApiResponse.Ok(message);
        response.RequestId = RequestId;
        return Ok(response);
    }

    /// <summary>202 接受（异步操作）</summary>
    protected ActionResult<ApiResponse<T>> AcceptedData<T>(T data, string? message = null)
    {
        var response = ApiResponse<T>.Ok(data, message);
        response.RequestId = RequestId;
        return Accepted(response);
    }
}
