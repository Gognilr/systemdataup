namespace BackupMonitor.Shared.Models.Admin;

/// <summary>枚举值计数项（报表通用）</summary>
public class EnumCountDto
{
    /// <summary>snake_case 枚举值</summary>
    public string Value { get; set; } = null!;

    public int Count { get; set; }
}

/// <summary>按日统计项</summary>
public class DailyCountDto
{
    public DateTime Date { get; set; }

    public int Count { get; set; }

    public long Bytes { get; set; }
}

/// <summary>客户端状态汇总报表</summary>
public class ClientSummaryReportDto
{
    public int TotalClients { get; set; }

    /// <summary>按 client_status 分组计数</summary>
    public List<EnumCountDto> ByStatus { get; set; } = [];
}

/// <summary>备份统计汇总报表</summary>
public class BackupSummaryReportDto
{
    public int TotalBackupSets { get; set; }

    public long TotalBytes { get; set; }

    /// <summary>按 backup_set_status 分组计数</summary>
    public List<EnumCountDto> ByStatus { get; set; } = [];

    /// <summary>最近 14 天按日上传统计</summary>
    public List<DailyCountDto> DailyUploads { get; set; } = [];
}

/// <summary>告警汇总报表</summary>
public class AlertSummaryReportDto
{
    /// <summary>活动告警数（open/acknowledged/in_progress）</summary>
    public int TotalActive { get; set; }

    /// <summary>活动告警按等级分组</summary>
    public List<EnumCountDto> ByLevel { get; set; } = [];

    /// <summary>全部告警按状态分组</summary>
    public List<EnumCountDto> ByStatus { get; set; } = [];
}

/// <summary>任务执行汇总报表</summary>
public class TaskSummaryReportDto
{
    public int TotalTasks { get; set; }

    /// <summary>最近 7 天有新备份版本入库的任务数</summary>
    public int TasksWithRecentUploads { get; set; }

    /// <summary>最近 7 天入库备份集数</summary>
    public int RecentUploadSets { get; set; }

    /// <summary>最近 7 天入库字节数</summary>
    public long RecentUploadBytes { get; set; }

    /// <summary>最近一次成功入库（available）时间</summary>
    public DateTime? LastSuccessfulUploadAt { get; set; }
}
