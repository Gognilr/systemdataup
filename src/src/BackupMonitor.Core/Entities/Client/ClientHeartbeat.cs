namespace BackupMonitor.Core.Entities.Client;

/// <summary>
/// 客户端心跳记录，对应 client_heartbeats 表（按月范围分区，原始数据保留30天）。
/// 复合主键 (id, received_at) 以支持分区表要求（分区键必须包含在主键中）。
/// </summary>
public class ClientHeartbeat
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>服务端接收时间（分区键）</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>客户端本地时间</summary>
    public DateTime? ClientTime { get; set; }

    /// <summary>Agent 进程运行时长（秒）</summary>
    public long? AgentUptimeSeconds { get; set; }

    /// <summary>系统运行时长（秒）</summary>
    public long? SystemUptimeSeconds { get; set; }

    public decimal? CpuPercent { get; set; }

    public decimal? MemoryPercent { get; set; }

    public long? MemoryAvailableBytes { get; set; }

    /// <summary>Agent 进程内存占用（字节）</summary>
    public long? AgentMemoryBytes { get; set; }

    public long? NetworkSendBps { get; set; }

    public long? NetworkReceiveBps { get; set; }

    /// <summary>Agent 侧活动指令数</summary>
    public int? ActiveCommandCount { get; set; }

    /// <summary>Agent 侧活动上传数</summary>
    public int? ActiveUploadCount { get; set; }

    /// <summary>附加负载数据（jsonb）</summary>
    public string? Payload { get; set; }

    // 导航属性
    public Client Client { get; set; } = null!;
}
