namespace BackupMonitor.Core.Entities.Rbac;

/// <summary>系统权限定义，对应 permissions 表</summary>
public class Permission
{
    public Guid Id { get; set; }

    /// <summary>权限编码（唯一，如 tasks.upload）</summary>
    public string Code { get; set; } = null!;

    /// <summary>权限名称</summary>
    public string Name { get; set; } = null!;

    /// <summary>所属模块（如 tasks、backups）</summary>
    public string? Module { get; set; }

    public string? Description { get; set; }

    // 导航属性
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
