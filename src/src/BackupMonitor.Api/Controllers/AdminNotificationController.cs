using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>管理端通知：发送记录查询 + 渠道配置（第二批补充设计；实际发送通道后续实现）</summary>
[Route("api/v1/admin")]
public class AdminNotificationController : ApiBaseController
{
    private readonly INotificationService _notificationService;

    public AdminNotificationController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    /// <summary>通知发送记录列表</summary>
    [HttpGet("notification-deliveries")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.read")]
    public async Task<ActionResult<ApiResponse<PagedResult<NotificationDeliveryDto>>>> ListDeliveries(
        [FromQuery] NotificationDeliveryQuery query, CancellationToken ct)
    {
        var result = await _notificationService.GetDeliveriesAsync(query, ct);
        return OkData(result);
    }

    /// <summary>
    /// 通知渠道是否已配置（R5）。
    ///
    /// 概览页靠它决定要不要挂那条常驻横幅。权限用 alerts.read 而不是 system.manage：
    /// 「告警发不出去」是每一个看告警的人都该知道的事，而不只是能改配置的那个人。
    /// 响应里只有一个布尔值，不含任何渠道细节。
    /// </summary>
    [HttpGet("notification-settings/status")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:alerts.read")]
    public async Task<ActionResult<ApiResponse<NotificationChannelStatusDto>>> GetSettingsStatus(CancellationToken ct)
    {
        var configured = await _notificationService.HasEnabledChannelAsync(ct);
        return OkData(new NotificationChannelStatusDto { Configured = configured });
    }

    /// <summary>
    /// 查询通知渠道配置。
    /// 整改批次 C · C1：走 GetSettingsForDisplayAsync，凭据字段（SMTP 密码 / webhook 地址）
    /// 一律掩码返回，响应体里搜不到明文。
    /// </summary>
    [HttpGet("notification-settings")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<NotificationSettingsDto>>> GetSettings(CancellationToken ct)
    {
        var result = await _notificationService.GetSettingsForDisplayAsync(ct);
        return OkData(result);
    }

    /// <summary>更新通知渠道配置（存 system_settings）</summary>
    [HttpPut("notification-settings")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse<NotificationSettingsDto>>> UpdateSettings(
        [FromBody] NotificationSettingsDto request, CancellationToken ct)
    {
        var result = await _notificationService.UpdateSettingsAsync(request, ct);
        return OkData(result, "通知渠道配置已保存");
    }

    /// <summary>
    /// 整改批次 C · C2：发送测试邮件。用当前表单里的配置试发一封，
    /// 把 SMTP 服务器返回的错误原样带回界面（如 535 Authentication failed），
    /// 不必等到真实告警发送失败才发现配置错了。
    /// </summary>
    [HttpPost("notification-settings/test")]
    [Authorize(AuthenticationSchemes = "Bearer", Policy = "perm:system.manage")]
    public async Task<ActionResult<ApiResponse>> SendTestEmail(
        [FromBody] NotificationTestEmailRequest request, CancellationToken ct)
    {
        var email = request.Email;

        // 表单里的密码框可能仍是掩码（用户没有重新输入密码），换成库中当前保存的明文密码
        if (email.SmtpPassword == NotificationSecretMask.Unchanged)
        {
            var current = await _notificationService.GetSettingsAsync(ct);
            email.SmtpPassword = current.Email.SmtpPassword;
        }

        var recipient = string.IsNullOrWhiteSpace(request.Recipient)
            ? email.Recipients.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
            : request.Recipient;

        if (string.IsNullOrWhiteSpace(recipient))
            throw new ValidationFailedException("请指定测试收件人，或先在收件人列表里填一个邮箱地址");

        try
        {
            await NotificationDispatchWorker.SendTestEmailAsync(email, recipient, ct);
        }
        catch (Exception ex)
        {
            // SMTP 服务器返回的原始错误原样带回界面，不做二次包装
            throw new BusinessException("SMTP_TEST_FAILED", ex.Message, 400);
        }

        return OkMessage($"测试邮件已发送至 {recipient}");
    }
}
