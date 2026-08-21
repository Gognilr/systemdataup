using System.Net;
using System.Net.Sockets;

namespace BackupMonitor.Shared.Security;

/// <summary>
/// LAN-only enrollment/discovery 的网络边界判断。
/// 不解析或信任匿名请求提交的 X-Forwarded-For；调用方应传入已经由可信代理策略决定的对端地址。
/// </summary>
public static class LanNetworkPolicy
{
    public static bool IsPrivateOrLoopback(IPAddress? address)
    {
        if (address is null)
            return false;

        // Windows 套接字可能把 IPv4 对端表示为 IPv4-mapped IPv6 地址；
        // 先统一映射回 IPv4，再应用私有网络判断规则。
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 169 && bytes[1] == 254;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            // fc00::/7（ULA）和 fe80::/10（link-local）。
            return (bytes[0] & 0xFE) == 0xFC
                || bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
        }

        return false;
    }
}
