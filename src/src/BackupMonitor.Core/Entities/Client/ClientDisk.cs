namespace BackupMonitor.Core.Entities.Client;

/// <summary>客户端磁盘状态（每次心跳更新），对应 client_disks 表</summary>
public class ClientDisk
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>盘符（如 C:）</summary>
    public string DriveName { get; set; } = null!;

    public string? VolumeLabel { get; set; }

    /// <summary>文件系统类型（NTFS/ReFS/FAT32 等）</summary>
    public string? Filesystem { get; set; }

    public long? TotalBytes { get; set; }

    public long? FreeBytes { get; set; }

    /// <summary>是否为备份源盘（容量告警依据）</summary>
    public bool IsSourceVolume { get; set; }

    public DateTime SampledAt { get; set; }

    // 导航属性
    public Client Client { get; set; } = null!;
}
