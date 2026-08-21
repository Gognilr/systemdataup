namespace BackupMonitor.Shared.Models.Agent;

/// <summary>BackupMonitor LAN discovery wire protocol v1。</summary>
public static class LanDiscoveryProtocol
{
    public const string Name = "BackupMonitor.Discovery";
    public const int Version = 1;
    public const int DefaultPort = 45808;
}

/// <summary>客户端安装器发送的 UDP 发现请求。</summary>
public sealed class LanDiscoveryRequest
{
    public string Protocol { get; set; } = LanDiscoveryProtocol.Name;
    public int ProtocolVersion { get; set; } = LanDiscoveryProtocol.Version;
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>服务端返回的 UDP 发现响应。响应不包含令牌、密钥或数据库信息。</summary>
public sealed class LanDiscoveryResponse
{
    public string Protocol { get; set; } = LanDiscoveryProtocol.Name;
    public int ProtocolVersion { get; set; } = LanDiscoveryProtocol.Version;
    public string Nonce { get; set; } = string.Empty;
    public string ServerInstanceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "BackupMonitor Server";
    public string ApiAddress { get; set; } = string.Empty;
}
