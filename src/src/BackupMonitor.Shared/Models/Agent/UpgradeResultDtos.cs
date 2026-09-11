using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Agent;

/// <summary>
/// Agent 升级完成后的主动回报（R20）。
///
/// 这是整条升级链路上唯一能证明「换过去了」的一句话：新程序起来之后由**新进程**发出。
/// 旧实现拿「指令执行完了」当成功，而那时换文件的动作根本还没发生——
/// 这个 DTO 的存在就是为了不再犯那个错。
/// </summary>
public class AgentUpgradeResultRequest
{
    /// <summary>触发这次升级的指令 ID；updater 从暂存记录里带过来，为空表示来源不明。</summary>
    public Guid? CommandId { get; set; }

    /// <summary>succeeded / rolled_back / failed</summary>
    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = null!;

    /// <summary>升级目标版本号（暂存时记下的那个）。</summary>
    [MaxLength(64)]
    public string? TargetVersion { get; set; }

    /// <summary>本进程实际运行的版本号。它与 TargetVersion 一致才算真的换过去了。</summary>
    [MaxLength(64)]
    public string? RunningVersion { get; set; }

    /// <summary>失败/回滚时的步骤与原因，原样落进目标行，排障时不必上门看日志。</summary>
    [MaxLength(2000)]
    public string? Message { get; set; }
}
