using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Client;

/// <summary>客户端证书，对应 client_certificates 表</summary>
public class ClientCertificate
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>证书指纹（唯一）</summary>
    public string Thumbprint { get; set; } = null!;

    public string? SerialNumber { get; set; }

    public DateTime IssuedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public CertificateStatus Status { get; set; } = CertificateStatus.Active;

    public DateTime? RevokedAt { get; set; }

    public string? RevokeReason { get; set; }

    /// <summary>签发出的客户端证书（PEM），注册结果查询返回（V002）</summary>
    public string? CertificatePem { get; set; }

    // 导航属性
    public Client Client { get; set; } = null!;
}
