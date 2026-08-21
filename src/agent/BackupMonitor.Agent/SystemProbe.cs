using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Agent;

/// <summary>
/// Agent 运行状态探针：每次心跳采集系统 CPU/内存、Agent 自身资源、网络、磁盘、服务和 Windows 会话。
/// 采样使用 Windows 原生 API，不依赖性能计数器安装状态。
/// </summary>
public sealed class SystemProbe
{
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private DateTime? _lastSampleAt;
    private TimeSpan? _lastProcessCpu;
    private SystemTimes? _lastSystemTimes;
    private long? _lastNetworkSent;
    private long? _lastNetworkReceived;

    public HeartbeatRequest BuildHeartbeat(long configVersion, AgentConfigResponse? config, IReadOnlyCollection<Guid> activeCommands)
    {
        var now = DateTime.UtcNow;
        var process = Process.GetCurrentProcess();
        var metrics = BuildMetrics(process, now);
        var sourceRoots = config?.Tasks.Where(t => t.Enabled)
            .Select(t => t.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList() ?? [];

        return new HeartbeatRequest
        {
            ClientTime = now,
            AgentUptimeSeconds = Math.Max(0, (long)(now - _startedAt).TotalSeconds),
            SystemUptimeSeconds = Math.Max(0, Environment.TickCount64 / 1000),
            ConfigVersion = configVersion,
            Metrics = metrics,
            Disks = GetDisks(sourceRoots),
            ServiceStates = GetServices(config?.MonitoredServices ?? []),
            UserSessions = GetUserSessions(),
            ActiveCommands = activeCommands.ToList(),
            ActiveUploads = []
        };
    }

    public List<string> GetIpAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
                .Where(a => !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private HeartbeatMetricsDto BuildMetrics(Process process, DateTime now)
    {
        var memory = GetMemoryStatus();
        var processCpu = CalculateProcessCpu(process, now);
        var systemCpu = CalculateSystemCpu();
        var (sent, received) = GetNetworkTotals();
        var (sendBps, receiveBps) = CalculateNetworkRates(sent, received, now);

        decimal? memoryPercent = memory.TotalBytes > 0
            ? Math.Round((decimal)(memory.TotalBytes - memory.AvailableBytes) / memory.TotalBytes * 100, 2)
            : null;

        return new HeartbeatMetricsDto
        {
            CpuPercent = systemCpu,
            AgentCpuPercent = processCpu,
            MemoryPercent = memoryPercent is not null ? Math.Clamp(memoryPercent.Value, 0, 100) : null,
            MemoryTotalBytes = memory.TotalBytes > 0 ? memory.TotalBytes : null,
            MemoryAvailableBytes = memory.AvailableBytes > 0 ? memory.AvailableBytes : null,
            AgentMemoryBytes = process.WorkingSet64,
            NetworkSendBps = sendBps,
            NetworkReceiveBps = receiveBps
        };
    }

    private decimal? CalculateProcessCpu(Process process, DateTime now)
    {
        try
        {
            var current = process.TotalProcessorTime;
            var result = CalculatePercent(
                _lastProcessCpu?.TotalMilliseconds,
                current.TotalMilliseconds,
                _lastSampleAt,
                now,
                Environment.ProcessorCount);
            _lastProcessCpu = current;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private decimal? CalculateSystemCpu()
    {
        if (!TryGetSystemTimes(out var current))
            return null;

        decimal? result = null;
        if (_lastSystemTimes is not null)
        {
            var idle = current.Idle - _lastSystemTimes.Value.Idle;
            var kernel = current.Kernel - _lastSystemTimes.Value.Kernel;
            var user = current.User - _lastSystemTimes.Value.User;
            var total = kernel + user;
            if (total > 0 && idle <= total)
                result = Math.Round(Math.Clamp((decimal)(total - idle) / total * 100, 0, 100), 2);
        }

        _lastSystemTimes = current;
        return result;
    }

    private (long? SendBps, long? ReceiveBps) CalculateNetworkRates(long sent, long received, DateTime now)
    {
        long? sendBps = null;
        long? receiveBps = null;
        if (_lastSampleAt is not null)
        {
            var seconds = (now - _lastSampleAt.Value).TotalSeconds;
            if (seconds > 0)
            {
                if (_lastNetworkSent is not null && sent >= _lastNetworkSent.Value)
                    sendBps = Math.Max(0, (long)((sent - _lastNetworkSent.Value) / seconds));
                if (_lastNetworkReceived is not null && received >= _lastNetworkReceived.Value)
                    receiveBps = Math.Max(0, (long)((received - _lastNetworkReceived.Value) / seconds));
            }
        }

        _lastNetworkSent = sent;
        _lastNetworkReceived = received;
        _lastSampleAt = now;
        return (sendBps, receiveBps);
    }

    private static decimal? CalculatePercent(
        double? previousWork,
        double currentWork,
        DateTime? previousAt,
        DateTime currentAt,
        int divisor)
    {
        if (previousWork is null || previousAt is null || divisor <= 0)
            return null;

        var wallMilliseconds = (currentAt - previousAt.Value).TotalMilliseconds;
        var workMilliseconds = currentWork - previousWork.Value;
        if (wallMilliseconds <= 0 || workMilliseconds < 0)
            return null;

        return Math.Round(Math.Clamp((decimal)(workMilliseconds / wallMilliseconds / divisor * 100), 0, 100), 2);
    }

    private static (long TotalBytes, long AvailableBytes) GetMemoryStatus()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return (ToInt64(status.TotalPhysicalMemory), ToInt64(status.AvailablePhysicalMemory));
        }

        try
        {
            var fallback = GC.GetGCMemoryInfo();
            return (0, (long)Math.Min(fallback.TotalAvailableMemoryBytes, long.MaxValue));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (long Sent, long Received) GetNetworkTotals()
    {
        long sent = 0;
        long received = 0;
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                var statistics = network.GetIPStatistics();
                sent = SaturatingAdd(sent, statistics.BytesSent);
                received = SaturatingAdd(received, statistics.BytesReceived);
            }
        }
        catch
        {
            // 网络统计权限或驱动异常不应阻断心跳。
        }

        return (sent, received);
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + Math.Max(0, right);

    private static long ToInt64(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    private static List<HeartbeatDiskDto> GetDisks(IEnumerable<string> sourceRoots)
    {
        var normalizedSources = sourceRoots
            .Select(GetRoot)
            .Where(r => r is not null)
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<HeartbeatDiskDto>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                result.Add(new HeartbeatDiskDto
                {
                    DriveName = drive.Name,
                    VolumeLabel = drive.IsReady ? drive.VolumeLabel : null,
                    Filesystem = drive.IsReady ? drive.DriveFormat : null,
                    TotalBytes = drive.IsReady ? drive.TotalSize : null,
                    FreeBytes = drive.IsReady ? drive.AvailableFreeSpace : null,
                    IsSourceVolume = normalizedSources.Contains(drive.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase)
                });
            }
            catch
            {
                // 可移动盘或权限不足的磁盘不阻断心跳。
            }
        }

        return result;
    }

    private static List<HeartbeatServiceStateDto> GetServices(IEnumerable<AgentMonitoredServiceDto> definitions)
    {
        var services = new List<HeartbeatServiceStateDto>();
        foreach (var definition in definitions)
        {
            try
            {
                using var service = new ServiceController(definition.ServiceName);
                var state = service.Status switch
                {
                    ServiceControllerStatus.Running => "running",
                    ServiceControllerStatus.Paused => "paused",
                    ServiceControllerStatus.Stopped => "stopped",
                    _ => "stopped"
                };
                services.Add(new HeartbeatServiceStateDto { ServiceName = definition.ServiceName, ActualState = state });
            }
            catch
            {
                services.Add(new HeartbeatServiceStateDto { ServiceName = definition.ServiceName, ActualState = "not_found" });
            }
        }

        return services;
    }

    private static List<HeartbeatUserSessionDto> GetUserSessions()
    {
        if (!OperatingSystem.IsWindows() || !WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
            return [];

        var result = new List<HeartbeatUserSessionDto>();
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(buffer, index * size));
                var username = QueryString(info.SessionId, WtsInfoClass.UserName);
                if (string.IsNullOrWhiteSpace(username))
                    continue;

                var clientName = QueryString(info.SessionId, WtsInfoClass.ClientName);
                var clientAddress = QueryClientAddress(info.SessionId);
                result.Add(new HeartbeatUserSessionDto
                {
                    SessionId = info.SessionId,
                    Username = username,
                    Domain = QueryString(info.SessionId, WtsInfoClass.DomainName),
                    State = MapSessionState(info.State),
                    ClientName = clientName,
                    ClientAddress = clientAddress,
                    IsRemote = !string.IsNullOrWhiteSpace(clientName) || !string.IsNullOrWhiteSpace(clientAddress),
                    LogonAt = QueryFileTime(info.SessionId, WtsInfoClass.LogonTime)
                });
            }
        }
        catch
        {
            // 会话枚举受权限或系统版本影响时，仍保持心跳可用。
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    private static string? QueryString(int sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            return null;
        try
        {
            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0');
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static DateTime? QueryFileTime(int sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes) || bytes < 8)
            return null;
        try
        {
            var fileTime = Marshal.PtrToStructure<NativeFileTime>(buffer);
            var value = ((long)fileTime.High << 32) | fileTime.Low;
            return value > 0 ? DateTime.FromFileTimeUtc(value) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? QueryClientAddress(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.ClientAddress, out var buffer, out var bytes) || bytes < Marshal.SizeOf<WtsClientAddress>())
            return null;
        try
        {
            var address = Marshal.PtrToStructure<WtsClientAddress>(buffer);
            if (address.Address is null)
                return null;
            if (address.AddressFamily == 2 && address.Address.Length >= 6)
                return string.Join('.', address.Address.Skip(2).Take(4));
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string MapSessionState(WtsConnectState state) => state switch
    {
        WtsConnectState.Active => "active",
        WtsConnectState.Connected => "connected",
        WtsConnectState.Disconnected => "disconnected",
        WtsConnectState.Idle => "idle",
        WtsConnectState.Listen => "listen",
        WtsConnectState.Reset => "reset",
        WtsConnectState.Down => "down",
        WtsConnectState.Init => "init",
        _ => "unknown"
    };

    private static string? GetRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(path);
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsClientAddress
    {
        public int AddressFamily;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[]? Address;
    }

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7,
        ClientName = 10,
        ClientAddress = 14,
        LogonTime = 18
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

    private static bool TryGetSystemTimes(out SystemTimes times)
    {
        times = default;
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user))
            return false;

        times = new SystemTimes(
            ((ulong)idle.High << 32) | idle.Low,
            ((ulong)kernel.High << 32) | kernel.Low,
            ((ulong)user.High << 32) | user.Low);
        return true;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr serverHandle,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr serverHandle,
        int sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
