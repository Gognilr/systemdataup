namespace BackupMonitor.Core.Enums;

/// <summary>
/// 上传会话状态的分类判定，集中一处。
///
/// 存在的理由是一次真实的故障：「暂停」按钮按下去，几秒后传输自己又跑起来了。
/// 根因是这套分类当时有四份互不知情的副本——UploadSessionService 的可写状态、
/// UploadSessionControlService 的可暂停/可取消状态、各查询服务的「在传」列表，
/// 以及**一条裸 SQL 里的字符串字面量**。前三份都把 paused 排除在可写之外，
/// 第四份不知道 paused 的存在，每收到一个分块就无条件把状态写回 uploading，
/// 于是暂停被在途分块抹掉，从来没有真正生效过一次。
///
/// 所以这里不只是「整理代码」：任何一个绕过这份定义的写入点，都会以同样的方式
/// 悄悄失效，而且不会有任何报错。新增状态判断请加在这个文件里，不要在调用点就地写数组。
/// </summary>
public static class UploadSessionStatuses
{
    /// <summary>
    /// 允许写入分块的状态。paused 刻意不在里面——暂停必须真的能停住写入，
    /// 否则这个状态只是个标签，上面那个按钮也就没有意义。
    /// </summary>
    public static readonly UploadStatus[] Writable =
        [UploadStatus.Created, UploadStatus.Uploading, UploadStatus.RetryWait];

    /// <summary>
    /// 占着并发额度与暂存空间的状态。暂停中的会话仍然占着这两样，
    /// 因此并发上限的判定要算上它——否则暂停一个就能再开一个，暂存目录会被撑爆。
    /// </summary>
    public static readonly UploadStatus[] Active =
        [UploadStatus.Created, UploadStatus.Uploading, UploadStatus.Paused, UploadStatus.RetryWait];

    /// <summary>能被暂停的状态：还在传或等着传的。</summary>
    public static readonly UploadStatus[] Pausable =
        [UploadStatus.Created, UploadStatus.WaitingPermission, UploadStatus.Uploading, UploadStatus.RetryWait];

    /// <summary>能被取消的状态：还没入库的都算。</summary>
    public static readonly UploadStatus[] Cancellable =
    [
        UploadStatus.Created, UploadStatus.WaitingPermission, UploadStatus.Uploading,
        UploadStatus.Paused, UploadStatus.RetryWait, UploadStatus.Received
    ];

    /// <summary>
    /// 「还没走完」的状态，管理端列表与统计口径。比 <see cref="Active"/> 多了
    /// waiting_permission / received / verifying——那几个不占暂存写入，但人还在等它。
    /// </summary>
    public static readonly UploadStatus[] InFlight =
    [
        UploadStatus.Created, UploadStatus.WaitingPermission, UploadStatus.Uploading,
        UploadStatus.Paused, UploadStatus.RetryWait, UploadStatus.Received, UploadStatus.Verifying
    ];

    /// <summary>已经终结、不该再被任何一方改写的状态。</summary>
    public static readonly UploadStatus[] Terminal =
        [UploadStatus.Committed, UploadStatus.Cancelled, UploadStatus.Expired, UploadStatus.Failed];

    public static bool CanWrite(UploadStatus status) => Writable.Contains(status);

    public static bool IsTerminal(UploadStatus status) => Terminal.Contains(status);
}
