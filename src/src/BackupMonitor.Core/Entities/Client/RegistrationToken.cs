using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Client;

/// <summary>客户端注册令牌（只存哈希，明文不得入库），对应 registration_tokens 表</summary>
public class RegistrationToken
{
    public Guid Id { get; set; }

    /// <summary>令牌哈希（明文仅在生成时一次性返回）</summary>
    public string TokenHash { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>令牌绑定的默认分组</summary>
    public Guid? ClientGroupId { get; set; }

    public DateTime? ExpiresAt { get; set; }

    /// <summary>最大使用次数</summary>
    public int MaxUses { get; set; } = 1;

    /// <summary>已使用次数</summary>
    public int UsedCount { get; set; }

    public RegistrationTokenStatus Status { get; set; } = RegistrationTokenStatus.Active;

    /// <summary>创建人</summary>
    public Guid? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    // 导航属性
    public ClientGroup? ClientGroup { get; set; }
    public Rbac.User? CreatedByUser { get; set; }
}
