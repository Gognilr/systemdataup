namespace BackupMonitor.Core.Enums;

/// <summary>
/// 备份集状态的分类判定，集中一处——理由与 <see cref="UploadSessionStatuses"/> 完全相同。
///
/// 这一份要挡的是「删了就再也备不回来」：删除备份集是软删（回收站 → recycle_bin，
/// 彻底删除 → deleted），两种删法行都还在表里。原先有六处
/// <c>BackupSets.Any(b =&gt; b.SourceCandidateId == ...)</c> 各写各的、全都不看状态，
/// 加上数据库那条无条件唯一约束，合起来的效果是：源文件没变的那份备份，
/// 删掉之后永久无法重新入库，而使用者在界面上看到的只是「没有新备份」——
/// 他刚刚删掉的正是那一份。
///
/// 任何绕过这份定义就地写条件的地方，都会以同样的形式静默失效，且不会有任何报错。
/// 新增状态判断请加在这个文件里，不要在调用点就地写数组。
/// </summary>
public static class BackupSetStatuses
{
    /// <summary>
    /// 已经被删除、不再占用「这个候选已经入过库」这个位置的状态。
    /// recycle_bin 也算：回收站的语义是「删了，但还能捞回来」，
    /// 而人删掉一份备份之后的正常预期就是还能再备一次。
    /// </summary>
    public static readonly BackupSetStatus[] Removed =
        [BackupSetStatus.RecycleBin, BackupSetStatus.Deleted];

    /// <summary>
    /// 「这个备份版本还活着」的判定口径：校验中、可用、校验失败、已隔离。
    ///
    /// 刻意用「全体减去 <see cref="Removed"/>」而不是逐个列举——以后往
    /// <see cref="BackupSetStatus"/> 里加一个新状态时，逐个列举的写法会把它悄悄
    /// 判成「已删除」，于是那个状态下的备份会被当成不存在而允许重复入库，
    /// 直到数据库的部分唯一索引在上传的最后一步拒绝它。
    ///
    /// 与数据库端的 uq_backup_sets_candidate_live（V033）是同一个口径，改一边就会对不上。
    /// </summary>
    public static readonly BackupSetStatus[] Live =
        Enum.GetValues<BackupSetStatus>().Except(Removed).ToArray();

    /// <summary>这个备份版本是不是还活着。</summary>
    public static bool IsLive(BackupSetStatus status) => !Removed.Contains(status);
}
