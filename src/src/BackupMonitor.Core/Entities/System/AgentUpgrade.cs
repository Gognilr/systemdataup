namespace BackupMonitor.Core.Entities.System;

/// <summary>
/// 一次分批升级下发（V041 · 整改清单 R20），对应 agent_upgrades 表。
///
/// 它存在的理由只有一条：**「指令执行完了」不是成功**。
/// 旧实现下发一条指令、Agent 回一句「包解压好了」，界面就显示成功，
/// 而机器上运行的还是旧程序。现在成功的判据挪到了客户端自报的版本号上，
/// 判定要跨越好几次心跳，因此必须有一张表把这段过程记住。
/// </summary>
public class AgentUpgrade
{
    public Guid Id { get; set; }

    public string TargetVersion { get; set; } = null!;

    public string PackageUrl { get; set; } = null!;

    public string PackageSha256 { get; set; } = null!;

    public string? Note { get; set; }

    public Core.Enums.AgentUpgradeStatus Status { get; set; }

    /// <summary>每批台数，逗号分隔，末位 0 表示「剩下的全放」。</summary>
    public string BatchPlan { get; set; } = "1,5,0";

    /// <summary>当前正在观察的批次序号；-1 表示一批都还没发。</summary>
    public int CurrentBatch { get; set; } = -1;

    public DateTime? BatchStartedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public string? CreatedByName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>整个下发被判失败的原因；界面上要能一眼看出「为什么后面的批次没推」。</summary>
    public string? FailureReason { get; set; }

    public List<AgentUpgradeTarget> Targets { get; set; } = [];
}

/// <summary>一次升级下发里的一台机器（V041 · R20），对应 agent_upgrade_targets 表。</summary>
public class AgentUpgradeTarget
{
    public Guid Id { get; set; }

    public Guid UpgradeId { get; set; }

    public Guid ClientId { get; set; }

    public int BatchIndex { get; set; }

    public Core.Enums.AgentUpgradeTargetStatus Status { get; set; }

    public Guid? CommandId { get; set; }

    /// <summary>
    /// 下发那一刻库里记的版本号。
    ///
    /// 判成功时要拿它和现在的版本号比：目标机器本来就是目标版本时，
    /// 「版本号等于目标版本」什么都证明不了——它从头到尾就没变过。
    /// </summary>
    public string? VersionBefore { get; set; }

    public string? ReportedVersion { get; set; }

    public DateTime? DispatchedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}
