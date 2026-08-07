namespace BackupMonitor.Core.Entities.Rbac;

/// <summary>角色-权限关联，对应 role_permissions 表（复合主键）</summary>
public class RolePermission
{
    public Guid RoleId { get; set; }

    public Guid PermissionId { get; set; }

    public DateTime CreatedAt { get; set; }

    // 导航属性
    public Role Role { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
}
