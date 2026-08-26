using BackupMonitor.Core.Entities.Rbac;
using BackupMonitor.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupMonitor.Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Username).HasColumnName("username").HasMaxLength(64).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.PasswordHash).HasColumnName("password_hash").HasMaxLength(255).IsRequired();
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(255);
        builder.Property(e => e.Mobile).HasColumnName("mobile").HasMaxLength(32);
        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.FailedLoginCount).HasColumnName("failed_login_count").HasDefaultValue(0);
        builder.Property(e => e.LockedUntil).HasColumnName("locked_until");
        builder.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
        builder.Property(e => e.PasswordChangedAt).HasColumnName("password_changed_at").HasDefaultValueSql("now()");
        // 不配置 HasDefaultValue(false)：false 恰为 CLR 默认值（批 1 哨兵值教训），
        // 否则改密成功后显式写 false 会被 EF 视为"未设置"而从 UPDATE 中省略，标志永远清不掉。
        builder.Property(e => e.MustChangePassword).HasColumnName("must_change_password");
        builder.Property(e => e.MfaEnabled).HasColumnName("mfa_enabled").HasDefaultValue(false);
        // 整改批次 C · C4：不配置 HasDefaultValue(0)——0 恰为 CLR 默认值，写显式 0（如登出/改密后
        // 端外把它重置为 0 的极端场景）会被 EF 当成"未设置"从 UPDATE 中省略，与上面 MustChangePassword
        // 同样的哨兵值教训。TokenVersion 只会递增，不会写回 0，这里仍按同一原则处理以防万一。
        builder.Property(e => e.TokenVersion).HasColumnName("token_version");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();
        builder.Property(e => e.RowVersion).HasColumnName("row_version").HasDefaultValue(1L).IsConcurrencyToken();

        builder.HasIndex(e => e.Username).IsUnique().HasDatabaseName("uq_users_username");
        builder.HasIndex(e => e.Status).HasDatabaseName("idx_users_status");
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(e => e.IsSystem).HasColumnName("is_system").HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("uq_roles_code");
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Code).HasColumnName("code").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Module).HasColumnName("module").HasMaxLength(64);
        builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(500);

        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("uq_permissions_code");
    }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.UserId).HasColumnName("user_id");
        builder.Property(e => e.RoleId).HasColumnName("role_id");
        builder.Property(e => e.ScopeType).HasColumnName("scope_type");
        builder.Property(e => e.ScopeId).HasColumnName("scope_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        // 注意：数据库唯一约束为 (user_id, role_id, scope_type, COALESCE(scope_id, 零值UUID))，
        // 表达式约束无法通过 EF Fluent API 表达，以 SQL 脚本为准。
        builder.HasIndex(e => e.UserId).HasDatabaseName("idx_user_roles_user_id");
        builder.HasIndex(e => e.RoleId).HasDatabaseName("idx_user_roles_role_id");

        builder.HasOne(e => e.User)
            .WithMany(e => e.UserRoles)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Role)
            .WithMany(e => e.UserRoles)
            .HasForeignKey(e => e.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.ClientGroup)
            .WithMany()
            .HasForeignKey(e => e.ScopeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");

        builder.HasKey(e => new { e.RoleId, e.PermissionId })
            .HasName("pk_role_permissions");
        builder.Property(e => e.RoleId).HasColumnName("role_id");
        builder.Property(e => e.PermissionId).HasColumnName("permission_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        builder.HasIndex(e => e.RoleId).HasDatabaseName("idx_role_permissions_role_id");
        builder.HasIndex(e => e.PermissionId).HasDatabaseName("idx_role_permissions_permission_id");

        builder.HasOne(e => e.Role)
            .WithMany(e => e.RolePermissions)
            .HasForeignKey(e => e.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Permission)
            .WithMany(e => e.RolePermissions)
            .HasForeignKey(e => e.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.UserId).HasColumnName("user_id");
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        builder.Property(e => e.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(64);
        builder.Property(e => e.ReplacedByHash).HasColumnName("replaced_by_hash").HasMaxLength(128);
        builder.Property(e => e.CreatedIp).HasColumnName("created_ip").HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasColumnName("user_agent").HasMaxLength(512);

        builder.HasIndex(e => e.TokenHash).IsUnique().HasDatabaseName("uq_refresh_tokens_hash");
        builder.HasIndex(e => e.UserId).HasDatabaseName("ix_refresh_tokens_user_id");
        builder.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_refresh_tokens_expires_at");

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
