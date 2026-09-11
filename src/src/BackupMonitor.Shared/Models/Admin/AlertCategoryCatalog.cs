namespace BackupMonitor.Shared.Models.Admin;

/// <summary>告警类别的一项：机器用的键 + 给人看的名字。</summary>
public sealed class AlertCategoryOptionDto
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>这一类通常出现在哪一档（供界面提示，不参与任何判定）。</summary>
    public string TypicalLevel { get; set; } = "warning";
}

/// <summary>
/// 全部告警类别。
///
/// 单开这份清单的理由：通知筛选要让人按类别勾选，而类别此前只以字符串字面量的形式
/// 散落在十几个服务里（<c>"client_resource"</c>、<c>"backup_missed"</c>…）。
/// 没有一处能回答「一共有哪些」——前端那份 <c>L.alert_category</c> 就漏了十来项，
/// 漏掉的那些在告警列表里直接显示成英文标识。
///
/// **新增告警类别时必须往这里加一行**，否则它在通知筛选里勾不到、
/// 在告警列表里显示成原始英文。
/// </summary>
public static class AlertCategoryCatalog
{
    public static IReadOnlyList<AlertCategoryOptionDto> All { get; } =
    [
        New("backup_missed", "到点没有备份", "warning"),
        New("precheck_failed", "备份检查未通过", "warning"),
        New("size_abnormal", "备份大小不对", "warning"),
        New("upload_failed", "备份上传失败", "warning"),
        New("upload_commit_failed", "存入备份库失败", "critical"),
        New("verification_failed", "备份复查未通过", "critical"),
        New("restore_verify_failed", "恢复前复查未通过", "critical"),
        New("client_offline", "客户端离线", "critical"),
        New("client_resource", "客户端资源吃紧（CPU / 内存 / 源盘）", "warning"),
        New("client_enrollment", "客户端登记", "notice"),
        New("service_state", "被监控的服务状态异常", "warning"),
        New("certificate_expiry", "客户端证书即将到期", "warning"),
        New("server_certificate_expiring", "服务端 TLS 证书即将到期", "warning"),
        New("client_ca_expiring", "客户端 CA 即将到期", "warning"),
        New("client_certificate_truncated", "客户端证书被 CA 截短", "warning"),
        New("agent_version_drift", "客户端版本落后", "warning"),
        New("agent_config_stale", "客户端一直没拿到新配置", "critical"),
        New("server_storage_low", "服务端磁盘不足", "warning"),
        New("server_storage_unavailable", "服务端存储不可用", "critical"),
        New("storage", "备份库异常", "warning"),
        New("retention_delete_failed", "过期备份删除失败", "warning"),
        New("retention_policy_invalid", "保留策略一条规则都没有", "critical"),
        New("retention_breaker_tripped", "保留清理已自动停手", "critical"),
        New("execution_item_timeout", "备份计划里的项目执行超时", "warning"),
        New("execution_item_queue_timeout", "备份计划里的项目一直没能开始", "warning"),
        New("config_backup_overdue", "配置备份超期未导出", "critical"),
        New("notification_channel_failed", "通知渠道发不出去", "critical"),
        New("system", "系统（升级下发等）", "critical")
    ];

    private static AlertCategoryOptionDto New(string key, string label, string typicalLevel) =>
        new() { Key = key, Label = label, TypicalLevel = typicalLevel };
}
