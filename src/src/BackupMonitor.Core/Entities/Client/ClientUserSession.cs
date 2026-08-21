namespace BackupMonitor.Core.Entities.Client;

/// <summary>客户端当前 Windows 用户会话，对应 client_user_sessions 表</summary>
public class ClientUserSession
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public int SessionId { get; set; }

    public string? Username { get; set; }

    public string? Domain { get; set; }

    /// <summary>active / connected / disconnected / idle 等</summary>
    public string State { get; set; } = null!;

    public string? ClientName { get; set; }

    public string? ClientAddress { get; set; }

    public bool IsRemote { get; set; }

    public DateTime? LogonAt { get; set; }

    public DateTime SampledAt { get; set; }

    public Client Client { get; set; } = null!;
}
