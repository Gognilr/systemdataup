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

    /// <summary>上一次上报的快照摘要（审计 B-09），仅心跳循环单线程访问</summary>
    private string? _lastSnapshotDigest;

    /// <summary>
    /// 组装一次心跳。
    ///
    /// 审计 B-09：磁盘 / 服务状态 / 用户会话三块算一个稳定摘要，与上次完全相同就整块不发。
    /// 心跳默认一分钟一次，而这三块在绝大多数时间里一个字节都不会变——
    /// 每次都带上等于把同一份数据重复写进 client_disks / client_services 几万遍。
    /// 指标（CPU/内存/网络）本来就每次都不同，不参与摘要。
    /// </summary>
    public HeartbeatRequest BuildHeartbeat(long configVersion, AgentConfigResponse? config, IReadOnlyCollection<Guid> activeCommands)
    {
        var now = DateTime.UtcNow;
        var process = Process.GetCurrentProcess();
        var metrics = BuildMetrics(process, now);
        var sourceRoots = config?.Tasks.Where(t => t.Enabled)
            .Select(t => t.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList() ?? [];

        var disks = GetDisks(sourceRoots);
        var services = GetServices(config?.MonitoredServices ?? []);
        var sessions = GetUserSessions();
        var ipAddresses = GetIpAddresses();

        var digest = ComputeSnapshotDigest(disks, services, sessions, ipAddresses);
        var unchanged = _lastSnapshotDigest is not null && _lastSnapshotDigest == digest;
        _lastSnapshotDigest = digest;

        return new HeartbeatRequest
        {
            ClientTime = now,
            AgentUptimeSeconds = Math.Max(0, (long)(now - _startedAt).TotalSeconds),
            SystemUptimeSeconds = Math.Max(0, Environment.TickCount64 / 1000),
            ConfigVersion = configVersion,
            Metrics = metrics,
            Disks = unchanged ? null : disks,
            ServiceStates = unchanged ? null : services,
            UserSessions = unchanged ? null : sessions,
            IpAddresses = unchanged ? null : ipAddresses,
            SnapshotUnchanged = unchanged,
            ActiveCommands = activeCommands.ToList(),
            ActiveUploads = []
        };
    }

    /// <summary>
    /// 四块快照的稳定摘要（审计 B-09）。
    ///
    /// 逐字段拼接而不是序列化整个对象：JSON 序列化会把 SampledAt 这类每次都变的字段
    /// 一起算进去，摘要就永远不相同，这个优化等于没做。
    /// </summary>
    private static string ComputeSnapshotDigest(
        List<HeartbeatDiskDto>? disks,
        List<HeartbeatServiceStateDto>? services,
        List<HeartbeatUserSessionDto>? sessions,
        List<string>? ipAddresses)
    {
        // 磁盘可用空间按 64MB 粒度取整：它每次采样都会有几 KB 的抖动，
        // 按字节比对会让摘要永不相同，而告警阈值是百分比，这个粒度足够。
        const long freeBytesGranularity = 64L * 1024 * 1024;

        // 分隔符用不会出现在盘符 / 服务名 / 用户名里的控制字符，
        // 避免「A|B」和「A」+「|B」拼出同一份原文。
        const char fieldSeparator = (char)1;
        const char itemSeparator = (char)2;
        const char sectionSeparator = (char)3;

        var builder = new System.Text.StringBuilder();

        foreach (var disk in (disks ?? []).OrderBy(d => d.DriveName, StringComparer.Ordinal))
        {
            builder.Append(disk.DriveName).Append(fieldSeparator)
                .Append(disk.VolumeLabel).Append(fieldSeparator)
                .Append(disk.Filesystem).Append(fieldSeparator)
                .Append(disk.TotalBytes).Append(fieldSeparator)
                .Append(disk.FreeBytes / freeBytesGranularity).Append(fieldSeparator)
                .Append(disk.IsSourceVolume ? '1' : '0').Append(itemSeparator);
        }

        builder.Append(sectionSeparator);
        foreach (var service in (services ?? []).OrderBy(s => s.ServiceName, StringComparer.Ordinal))
        {
            builder.Append(service.ServiceName).Append(fieldSeparator)
                .Append(service.ActualState).Append(fieldSeparator)
                .Append(service.StartType).Append(itemSeparator);
        }

        builder.Append(sectionSeparator);
        foreach (var session in (sessions ?? []).OrderBy(s => s.SessionId))
        {
            builder.Append(session.SessionId).Append(fieldSeparator)
                .Append(session.Username).Append(fieldSeparator)
                .Append(session.Domain).Append(fieldSeparator)
                .Append(session.State).Append(fieldSeparator)
                .Append(session.IsRemote ? '1' : '0').Append(itemSeparator);
        }

        // 网卡地址排序后入摘要：同一台机器上枚举顺序不保证稳定，
        // 不排序会让「地址没变」被误判成「变了」，每次心跳都白写一遍库。
        builder.Append(sectionSeparator);
        foreach (var ip in (ipAddresses ?? []).OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
            builder.Append(ip).Append(itemSeparator);

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// 本机自报的网卡地址（审计 D1）。
    ///
    /// 只留「别的机器真能拿它连上来」的地址：fe80:: 链路本地、fec0:: 站点本地和
    /// 169.254/16 APIPA 全部滤掉。它们要么带 %12 这样的作用域后缀、离开本网段就无意义，
    /// 要么根本是「没拿到 DHCP」的症状——留在列表里只会让详情抽屉里那一串没法看，
    /// 也让「客户端 IP」这一列显示出运维根本用不上的值。
    ///
    /// IPv4 排在 IPv6 前面：界面取的是第一个地址，而这套产品实际部署的局域网里
    /// 能让人照着敲进 mstsc 的永远是 IPv4。
    ///
    /// 过滤会改变 <see cref="ComputeSnapshotDigest"/> 的输入，升级后的首次心跳
    /// 必然因摘要变化重报一次完整快照——这是预期行为，只发生一次。
    /// </summary>
    public List<string> GetIpAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(IsReportableAddress)
                // OrderBy 是稳定排序：同族内部仍是原来的枚举顺序，这里只把 IPv4 整体提前，
                // 不去猜「哪块网卡更重要」——那是另一个问题，猜错比不猜更糟。
                .OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsReportableAddress(IPAddress address)
    {
        if (address.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork
            or System.Net.Sockets.AddressFamily.InterNetworkV6))
            return false;

        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal;

        // 169.254/16：DHCP 失败后 Windows 自己配上去的 APIPA 地址，
        // 它出现本身就说明这块网卡没接入网络。
        var bytes = address.GetAddressBytes();
        return bytes.Length != 4 || bytes[0] != 169 || bytes[1] != 254;
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
