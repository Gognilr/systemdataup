using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Rbac;

/// <summary>
/// 用户-角色分配（支持按分组限定范围），对应 user_roles 表。
/// 唯一性约束: (user_id, role_id, scope_type, COALESCE(scope_id, 零值 UUID))，
/// 该约束在 Fluent API 中无法直接表达，由数据库层保证。
/// </summary>
public class UserRole
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    /// <summary>作用范围类型：global 或 client_group</summary>
    public ScopeType ScopeType { get; set; } = ScopeType.Global;

    /// <summary>范围为 client_group 时的分组 ID</summary>
    public Guid? ScopeId { get; set; }

    public DateTime CreatedAt { get; set; }

    // 导航属性
    public User User { get; set; } = null!;
    public Role Role { get; set; } = null!;
    public Client.ClientGroup? ClientGroup { get; set; }
}
