using BackupMonitor.Core.Abstractions;
using BackupMonitor.Core.Entities.Client;
using BackupMonitor.Core.Enums;

namespace BackupMonitor.Core.Entities.Rbac;

/// <summary>系统管理用户（管理员、操作员等），对应 users 表</summary>
public class User : IHasRowVersion
{
    public Guid Id { get; set; }

    /// <summary>登录用户名（唯一）</summary>
    public string Username { get; set; } = null!;

    /// <summary>显示名称</summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>密码哈希（Argon2id/bcrypt/PBKDF2，禁止可逆加密）</summary>
    public string PasswordHash { get; set; } = null!;

    public string? Email { get; set; }

    public string? Mobile { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>连续登录失败次数</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>锁定截止时间</summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime PasswordChangedAt { get; set; }

    /// <summary>是否启用 MFA</summary>
    public bool MfaEnabled { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>乐观锁版本号</summary>
    public long RowVersion { get; set; }

    // 导航属性
    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RegistrationToken> CreatedRegistrationTokens { get; set; } = [];
}
