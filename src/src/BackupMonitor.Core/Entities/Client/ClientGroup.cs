namespace BackupMonitor.Core.Entities.Client;

/// <summary>客户端分组（树形结构），对应 client_groups 表</summary>
public class ClientGroup
{
    public Guid Id { get; set; }

    /// <summary>父分组 ID（顶级分组为 null）</summary>
    public Guid? ParentId { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>分组编码（唯一）</summary>
    public string Code { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>默认带宽限速（KB/s）</summary>
    public int? DefaultBandwidthLimitKbps { get; set; }

    /// <summary>默认并发数</summary>
    public int DefaultConcurrency { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // 导航属性
    public ClientGroup? Parent { get; set; }
    public ICollection<ClientGroup> Children { get; set; } = [];
    public ICollection<Core.Entities.Client.Client> Clients { get; set; } = [];
    public ICollection<RegistrationToken> RegistrationTokens { get; set; } = [];
}
