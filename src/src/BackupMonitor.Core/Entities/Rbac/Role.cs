namespace BackupMonitor.Core.Entities.Rbac;

/// <summary>RBAC 角色，内置 4 个系统角色，对应 roles 表</summary>
public class Role
{
    public Guid Id { get; set; }

    /// <summary>角色编码（唯一，如 system_admin）</summary>
    public string Code { get; set; } = null!;

    /// <summary>角色名称</summary>
    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>是否为系统内置角色（不可删除）</summary>
    public bool IsSystem { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // 导航属性
    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
