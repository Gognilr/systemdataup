using System.Runtime.InteropServices;
using System.Text;

namespace BackupMonitor.Agent.Updater;

/// <summary>
/// 在当前控制台登录会话里启动一个进程（用来换完文件后把托盘重新拉起来）。
///
/// 为什么需要这一段 P/Invoke：updater 是被 Agent 服务拉起来的，跑在会话 0，
/// 而托盘必须跑在用户的登录会话里才有托盘区可用。直接 Process.Start 出来的
/// 托盘进程在会话 0，用户永远看不见它，却又占着安装目录里的文件。
///
/// **整段都是尽力而为**：失败只是这次不重启托盘，下次登录时 HKLM\Run 的自启项
/// 会把它拉起来，而托盘跟备份能不能做没有关系。所以这里面任何一个失败
/// 都只返回 false，绝不往上抛。
/// </summary>
internal static class SessionLauncher
{
    /// <summary>
    /// 把托盘拉回到**每一个还连着的登录会话**里。
    ///
    /// 原先只认 <c>WTSGetActiveConsoleSessionId</c>——物理控制台那一个会话。
    /// 而这套系统的服务器几乎全是远程桌面进去运维的，**RDP 会话不是控制台会话**：
    /// 于是升级完托盘一个也起不来，现场表现是「升级成功了，但托盘没了」，
    /// 日志里只留下一句「当前没有可用的登录会话」——而那句话本身是错的，人明明在线。
    ///
    /// 现在枚举所有会话，往每一个 Active 的里面各起一个（多人同时登录时每人一个，
    /// 与开机自启的行为一致）。只要有一个起成功就算成功。
    /// </summary>
    public static bool TryLaunchInConsoleSession(string executablePath, string arguments, UpdaterLog log)
    {
        var launched = false;
        foreach (var sessionId in ActiveSessionIds(log))
        {
            if (TryLaunchInSession(sessionId, executablePath, arguments, log))
                launched = true;
        }

        return launched;
    }

    /// <summary>
    /// 还连着的会话：物理控制台 + 所有 WTSActive 的会话，去重。
    /// 断开的 RDP 会话（WTSDisconnected）不算——往一个没人看的会话里起托盘没有意义，
    /// 而且用户重连时会变成两个托盘。
    /// </summary>
    private static List<uint> ActiveSessionIds(UpdaterLog log)
    {
        var ids = new List<uint>();

        var console = WTSGetActiveConsoleSessionId();
        if (console != 0xFFFFFFFF)
            ids.Add(console);

        var buffer = IntPtr.Zero;
        try
        {
            if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buffer, out var count))
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                    if (entry.State == WTSActive && !ids.Contains(entry.SessionId))
                        ids.Add(entry.SessionId);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"枚举登录会话失败，只尝试控制台会话：{ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                WTSFreeMemory(buffer);
        }

        return ids;
    }

    private static bool TryLaunchInSession(uint sessionId, string executablePath, string arguments, UpdaterLog log)
    {
        var userToken = IntPtr.Zero;
        var primaryToken = IntPtr.Zero;
        var environment = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
                return false;   // 这个会话上没有可用的用户令牌（比如停在登录界面）

            var attributes = new SECURITY_ATTRIBUTES();
            attributes.Length = Marshal.SizeOf(attributes);
            if (!DuplicateTokenEx(userToken, MaximumAllowed, ref attributes,
                    SecurityImpersonation, TokenPrimary, out primaryToken))
                return false;

            if (!CreateEnvironmentBlock(out environment, primaryToken, inherit: false))
                environment = IntPtr.Zero;   // 环境块拿不到就用默认环境，不值得为它放弃

            var startupInfo = new STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf(startupInfo);
            startupInfo.lpDesktop = @"winsta0\default";

            // 命令行的第一段必须是程序自身（argv[0] 约定），否则托盘会把
            // 第一个参数当成程序名，后面的 --data-directory 全部错位。
            // CreateProcessAsUser 会就地改写命令行缓冲区，必须传可写的 StringBuilder；
            // 传 string 在某些运行时上会直接写坏托管堆。
            var commandLine = new StringBuilder($"\"{executablePath}\" {arguments}".TrimEnd());
            var created = CreateProcessAsUser(
                primaryToken,
                executablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateUnicodeEnvironment | CreateNoWindow | CreateNewConsole,
                environment,
                Path.GetDirectoryName(executablePath),
                ref startupInfo,
                out var processInfo);

            if (created)
            {
                CloseHandle(processInfo.hProcess);
                CloseHandle(processInfo.hThread);
                return true;
            }

            log.Warn($"在登录会话 {sessionId} 里启动托盘失败，错误码 {Marshal.GetLastWin32Error()}");
            return false;
        }
        catch (Exception ex)
        {
            log.Warn($"在登录会话里启动托盘时异常：{ex.Message}");
            return false;
        }
        finally
        {
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero)
                CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero)
                CloseHandle(userToken);
        }
    }

    private const int MaximumAllowed = 0x2000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewConsole = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int Length;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    /// <summary>WTS_CONNECTSTATE_CLASS.WTSActive —— 有人正连着在用。</summary>
    private const int WTSActive = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSEnumerateSessions(
        IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        int desiredAccess,
        ref SECURITY_ATTRIBUTES attributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        StringBuilder? commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
