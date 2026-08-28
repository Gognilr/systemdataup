using BackupMonitor.Core.Enums;

namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// 枚举值 → 给人看的中文说法。
///
/// 存在的理由：告警标题此前直接把 EnumMapping.ToSnakeCase 的结果拼进去，
/// 于是告警中心里挂着一条「precheck_task 指令执行失败」——收到这条告警的人
/// 既不知道 precheck_task 是什么，也不知道该做什么。内部标识用来对齐数据库和接口，
/// 不该出现在任何一个人要读的句子里。
///
/// 只覆盖会出现在告警/通知文案里的枚举；纯内部流转的状态不必翻译。
/// </summary>
public static class PlainText
{
    /// <summary>预检状态 → 这次检查到底怎么了</summary>
    public static string Of(PrecheckStatus status) => status switch
    {
        PrecheckStatus.NotScanned => "还没检查过",
        PrecheckStatus.Passed => "检查通过",
        PrecheckStatus.NoNewBackup => "没有新的备份文件",
        PrecheckStatus.StillChanging => "备份文件还在写入中",
        PrecheckStatus.RequiredFileMissing => "缺少必需文件，备份不完整",
        PrecheckStatus.SizeAbnormal => "备份大小超出设定范围",
        PrecheckStatus.PathNotFound => "找不到源路径",
        PrecheckStatus.AccessDenied => "没有权限读取源路径",
        PrecheckStatus.Failed => "检查失败",
        _ => "检查未通过"
    };

    /// <summary>告警等级 → 邮件正文里那一行。枚举原样拼进去会写成 Warning。</summary>
    public static string Of(AlertLevel level) => level switch
    {
        AlertLevel.Critical => "严重",
        AlertLevel.Warning => "警告",
        AlertLevel.Notice => "提示",
        _ => level.ToString()
    };

    /// <summary>指令类型 → 这条指令在替使用者做什么</summary>
    public static string Of(CommandType type) => type switch
    {
        CommandType.PrecheckTask => "检查备份文件",
        CommandType.PrecheckAll => "检查该客户端的全部备份",
        CommandType.UploadCandidate => "上传备份",
        CommandType.UploadLatest => "上传最新备份",
        CommandType.PauseUpload => "暂停上传",
        CommandType.ResumeUpload => "继续上传",
        CommandType.CancelUpload => "取消上传",
        CommandType.Rescan => "重新扫描",
        CommandType.Rehash => "重新计算校验值",
        CommandType.SyncConfig => "同步配置",
        CommandType.RefreshMetrics => "刷新运行指标",
        CommandType.UpgradeAgent => "升级客户端程序",
        CommandType.BrowsePath => "浏览目录",
        CommandType.ListServices => "列出系统服务",
        _ => "执行指令"
    };
}
