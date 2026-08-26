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

    /// <summary>是否强制修改口令（口令由系统代设时置 true，改密成功后清除；安装器设的初始管理员口令不置位，见 V017）</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>是否启用 MFA</summary>
    public bool MfaEnabled { get; set; }

    /// <summary>
    /// 令牌版本号（整改批次 C · C4）。改密、登出、（未来实现的）禁用账号/改角色/改权限
    /// 都要 ++，签发的 JWT 带同名 tv claim，TokenVersionMiddleware 据此在 60 秒内让
    /// 已撤销的旧令牌失效，不必等待其自然过期。
    /// </summary>
    public int TokenVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>乐观锁版本号</summary>
    public long RowVersion { get; set; }

    // 导航属性
    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RegistrationToken> CreatedRegistrationTokens { get; set; } = [];
}
