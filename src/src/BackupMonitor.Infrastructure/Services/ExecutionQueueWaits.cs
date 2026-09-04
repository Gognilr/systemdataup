using BackupMonitor.Core.Enums;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 「这一项此刻在等什么」的判据。
///
/// 单独抽出来是因为它有三个使用者——放行的人（SequentialExecutionWorker）、
/// 数数的人（UploadProgressService 的队列状态条）、列名单的人
/// （ExecutionQueueService.GetQueueAsync）。三份各写各的必然会漂：
/// 界面说「有空余名额」而队列就是不放行时，人无从判断系统是不是卡住了。
/// </summary>
public static class ExecutionQueueWaits
{
    /// <summary>已经开跑</summary>
    public const string Running = "running";

    /// <summary>在等本次执行自己的并发度（前一项还没跑完）——顺序执行的正常表现</summary>
    public const string WaitingInRun = "waiting_in_run";

    /// <summary>在等全局上传名额</summary>
    public const string WaitingForUploadSlot = "waiting_for_slot";

    /// <summary>
    /// 会建上传会话、因而占用全局上传名额的指令类型。
    ///
    /// 数组形态不是风格问题，是必须的：EF 翻译不了表达式树里的自定义方法调用，
    /// <c>Where(i =&gt; ConsumesUploadSlot(i.CommandType))</c> 会在**运行时**抛
    /// InvalidOperationException——而它所在的那一轮推进整个中止，队列从此不再往下走，
    /// 表现是「计划一直停在执行中」，日志里只有一行「顺序执行器轮询异常」。
    /// 进查询用这个数组的 Contains，内存里判断用下面那个方法，两者共用同一份定义。
    /// </summary>
    public static readonly CommandType[] UploadSlotCommandTypes =
        [CommandType.UploadCandidate, CommandType.UploadLatest];

    /// <summary>
    /// 这一项下发之后会不会建上传会话，也就是它占不占全局上传名额。
    /// 预检项（PrecheckTask）不占——它只让客户端扫目录算哈希。
    /// **不要在 IQueryable 里调用它**，理由见 <see cref="UploadSlotCommandTypes"/>。
    /// </summary>
    public static bool ConsumesUploadSlot(CommandType commandType) =>
        UploadSlotCommandTypes.Contains(commandType);

    /// <summary>
    /// 一个还没开跑的项在等什么。顺序与放行时的判断完全一致：
    /// 先看本次执行的并发度（本轮轮不到它），再看全局上传闸（只挡上传项）。
    /// </summary>
    public static string Classify(
        int runningInRun, int runMaxConcurrent, CommandType commandType, bool uploadSlotFull)
    {
        if (runningInRun >= runMaxConcurrent)
            return WaitingInRun;

        if (uploadSlotFull && ConsumesUploadSlot(commandType))
            return WaitingForUploadSlot;

        // 本轮就该被放行了，归到「等这次执行」而不是「等名额」
        return WaitingInRun;
    }
}
