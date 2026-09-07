namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// 大文件顺序读写的统一参数（实施方案 T6）。
///
/// 这个值原先只固化在 <c>UploadStorage.ChunkIoBufferBytes</c> 里，而恢复下载、
/// 整包 ZIP、定期复查三条链路仍然是 .NET 的 80KB 历史默认值——同样是「把一个几 GB 的
/// 文件从头读到尾」，两边没有理由用不同的读法。提到这里是为了让「1MB」只有一个出处，
/// 而不是四个各写一遍的字面值。
/// </summary>
public static class StreamIo
{
    /// <summary>
    /// 大文件顺序读写缓冲。
    ///
    /// 80KB 是 .NET 的历史默认值，对 GB 级文件意味着几万次读写往返（6GB / 80KB ≈ 7.8 万次）。
    /// 1MB 在实测里把纯写吞吐从 437MB/s 抬到 686MB/s；读侧配合 SequentialScan
    /// 让操作系统的预读真正成批下发。
    /// </summary>
    public const int LargeFileBufferBytes = 1024 * 1024;

    /// <summary>
    /// 顺序读一个大文件的标准打开方式。
    /// SequentialScan 是关键的一半：没有它，1MB 缓冲只是少了几次系统调用，
    /// 预读仍然按默认策略走。
    /// </summary>
    public static FileStream OpenSequentialRead(string path, FileShare share = FileShare.Read) =>
        new(path, FileMode.Open, FileAccess.Read, share,
            bufferSize: LargeFileBufferBytes,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
}
