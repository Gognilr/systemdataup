using System.Runtime.Versioning;
using System.ServiceProcess;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.Win32;

namespace BackupMonitor.Agent;

/// <summary>
/// 列出本机安装的 Windows 服务，供服务端配置"关键服务监控"时挑选。
///
/// 为什么值得单独做一条指令：服务名和显示名是两回事，而要填的是服务名。
/// SQL Server 默认实例叫 MSSQLSERVER，命名实例叫 MSSQL$SQLEXPRESS，
/// 代理服务又分别是 SQLSERVERAGENT 和 SQLAgent$SQLEXPRESS。凭记忆敲错一个字符，
/// 结果是监控了一个不存在的服务——而它的表现是状态永远 not_found，
/// 看起来像"服务挂了"，实际是名字写错了。这类错误几乎不可能靠人自查发现。
///
/// 与目录浏览同一条原则：让人指着选，而不是描述。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstalledServiceProbe
{
    private readonly ILogger<InstalledServiceProbe> _logger;

    public InstalledServiceProbe(ILogger<InstalledServiceProbe> logger)
    {
        _logger = logger;
    }

    public InstalledServiceListDto List(bool includeStopped, CancellationToken ct)
    {
        var result = new InstalledServiceListDto { CapturedAt = DateTime.UtcNow };

        ServiceController[] services;
        try
        {
            services = ServiceController.GetServices();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举 Windows 服务失败");
            throw new InvalidOperationException($"无法枚举 Windows 服务：{ex.Message}", ex);
        }

        try
        {
            foreach (var service in services)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var status = MapStatus(service.Status);
                    if (!includeStopped && status == "stopped")
                        continue;

                    result.Services.Add(new InstalledServiceDto
                    {
                        ServiceName = service.ServiceName,
                        DisplayName = string.IsNullOrWhiteSpace(service.DisplayName)
                            ? service.ServiceName
                            : service.DisplayName,
                        Status = status,
                        StartType = ReadStartType(service.ServiceName)
                    });
                }
                catch (Exception ex)
                {
                    // 个别服务读不到属性（权限或正在删除）不该让整份清单失败。
                    _logger.LogDebug(ex, "读取服务信息失败 service={Service}", service.ServiceName);
                }
            }
        }
        finally
        {
            foreach (var service in services)
                service.Dispose();
        }

        result.Services = result.Services
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result;
    }

    private static string MapStatus(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => "running",
        ServiceControllerStatus.Stopped => "stopped",
        ServiceControllerStatus.Paused => "paused",
        ServiceControllerStatus.StartPending => "start_pending",
        ServiceControllerStatus.StopPending => "stop_pending",
        ServiceControllerStatus.PausePending => "pause_pending",
        ServiceControllerStatus.ContinuePending => "continue_pending",
        _ => "unknown"
    };

    /// <summary>
    /// 启动类型只能从注册表读——ServiceController 没有暴露它。
    /// 读不到就返回 null，界面显示"未知"，不影响挑选。
    /// </summary>
    private static string? ReadStartType(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
            if (key?.GetValue("Start") is not int start)
                return null;

            // 用 auto 而不是 automatic：service_start_type 这个域和 ServiceStartType
            // 枚举用的都是 auto，同一个概念在同一个界面上不该有两种叫法。
            // boot / system 保留原样——它们确实不是"自动启动"，在挑选界面上如实显示更有用。
            return start switch
            {
                0 => "boot",
                1 => "system",
                2 => "auto",
                3 => "manual",
                4 => "disabled",
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
