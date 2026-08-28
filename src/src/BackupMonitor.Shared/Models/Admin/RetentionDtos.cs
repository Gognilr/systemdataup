using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>保留策略（第二批补充设计；数据表 retention_policies）</summary>
public class RetentionPolicyDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>保留最近 N 个版本</summary>
    public int? KeepLastCount { get; set; }

    /// <summary>保留周版本数</summary>
    public int? KeepWeeklyCount { get; set; }

    /// <summary>保留月末版本数</summary>
    public int? KeepMonthlyCount { get; set; }

    /// <summary>保留年度版本数</summary>
    public int? KeepYearlyCount { get; set; }

    /// <summary>最短保留天数</summary>
    public int MinimumRetentionDays { get; set; }

    /// <summary>回收区保留天数</summary>
    public int RecycleBinDays { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>引用该策略的备份任务数量</summary>
    public int BoundTaskCount { get; set; }

    /// <summary>是否为「新建任务默认绑定」的那份策略（system_settings.default_retention_policy_id）</summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// 创建/更新保留策略请求。
///
/// 审查 P1-2：四个 Keep*Count 全可空、两个天数下限为 0 时，
/// 「四项留空 + 两个 0」是一份能通过全部 [Range] 校验的合法策略，
/// 其效果是 GFS 保留集为空 → 该任务全部备份集进回收站 → 保留 0 天 → 下一轮全部物理删除。
/// 因此下限提到 1，并用 IValidatableObject 加「至少一条保留规则」的跨字段校验。
/// </summary>
public class RetentionPolicyUpsertDto : IValidatableObject
{
    [Required(ErrorMessage = "name 必填")]
    [MaxLength(128, ErrorMessage = "name 不能超过 128 字符")]
    public string Name { get; set; } = null!;

    [Range(1, int.MaxValue, ErrorMessage = "keepLastCount 必须大于 0")]
    public int? KeepLastCount { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "keepWeeklyCount 必须大于 0")]
    public int? KeepWeeklyCount { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "keepMonthlyCount 必须大于 0")]
    public int? KeepMonthlyCount { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "keepYearlyCount 必须大于 0")]
    public int? KeepYearlyCount { get; set; }

    /// <summary>
    /// 最短保留天数：不满该天数的备份集一律不回收。默认 0（V027）——
    /// 它是一条会覆盖「保留最近 N 份」的规则，原来的默认值 30 意味着每天备份的任务
    /// 实际上会攒到 30 份以上，配了「只留 3 份」也没用。下限保持 0：
    /// 此时仍有至少一条 Keep*Count 规则兜底（见 Validate），不存在「一入库即可被清理」。
    /// </summary>
    [Range(0, 36500, ErrorMessage = "minimumRetentionDays 必须在 0~36500 之间")]
    public int MinimumRetentionDays { get; set; }

    /// <summary>回收区保留天数。下限 1——0 意味着进回收站的下一轮就物理删除，等于没有回收站。</summary>
    [Range(1, 3650, ErrorMessage = "recycleBinDays 必须在 1~3650 之间")]
    public int RecycleBinDays { get; set; } = 7;

    /// <summary>跨字段校验：至少要有一条 Keep*Count 规则，否则 GFS 保留集恒为空。</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (KeepLastCount is null or <= 0 &&
            KeepWeeklyCount is null or <= 0 &&
            KeepMonthlyCount is null or <= 0 &&
            KeepYearlyCount is null or <= 0)
        {
            yield return new ValidationResult(
                "至少需要设置一条保留规则（keepLastCount / keepWeeklyCount / keepMonthlyCount / keepYearlyCount 之一），" +
                "否则该策略下的备份集会在保留期满后被全部清除",
                [
                    nameof(KeepLastCount), nameof(KeepWeeklyCount),
                    nameof(KeepMonthlyCount), nameof(KeepYearlyCount)
                ]);
        }
    }
}
