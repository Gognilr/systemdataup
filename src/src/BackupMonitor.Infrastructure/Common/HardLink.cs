using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// NTFS 硬链接。入库阶段用它代替「把整份暂存文件复制进仓库」：
/// 6GB 的备份集原本是 6GB 读 + 6GB 写，建链接则是瞬间完成的元数据操作。
///
/// 为什么是硬链接而不是 File.Move：**暂存文件仍然在**（链接计数为 2）。
/// 入库是个多步过程（复制 → 写 manifest → 落库 → 原子重命名），中间任何一步失败都要能重试；
/// Move 走过之后暂存已经没了，重试无从谈起。硬链接则是「多了一个名字指向同一份数据」，
/// 失败时删掉仓库那个名字即可，暂存原封不动。
///
/// 共享同一份数据是否有风险：暂存文件在提交之后只会被删除、不会被改写
/// （分块写入发生在提交之前，LifecycleExpiryWorker 只做删除），因此是安全的。
/// 删掉暂存那个名字也不会影响仓库里的数据——链接计数减一而已。
/// </summary>
public static class HardLink
{
    /// <summary>
    /// 尽力建立硬链接。返回 false 表示这条路走不通（跨卷、非 NTFS、非 Windows、
    /// 权限不足……），调用方应回退到复制。任何失败都不抛异常：
    /// 这是一条优化路径，它自己不能成为入库失败的原因。
    /// </summary>
    public static bool TryCreate(string existingFilePath, string newLinkPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        // 跨卷的硬链接不存在。先比一次盘符，省掉一次注定失败的系统调用，
        // 也让「暂存与仓库配在不同卷上」这个常见部署直接走复制路径。
        var existingRoot = Path.GetPathRoot(Path.GetFullPath(existingFilePath));
        var newRoot = Path.GetPathRoot(Path.GetFullPath(newLinkPath));
        if (!string.Equals(existingRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return CreateHardLinkW(newLinkPath, existingFilePath, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
