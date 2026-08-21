-- =============================================================
-- V005：管理员账户引导
--
-- 该脚本由 BackupMonitor.Server.Setup.MigrationRunner 执行。
-- ${ADMIN_PASSWORD} 只会被替换为 Npgsql 参数 @admin_password，绝不拼接口令文本。
-- 升级时如果 V005 已经应用，运行器只校验并兼容已知旧 checksum，不会重置现有口令。
-- =============================================================

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS must_change_password boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN users.must_change_password IS
    '首次登录是否强制修改口令（初始管理员为 true，改密成功后清除）';

INSERT INTO users (
    id,
    username,
    display_name,
    password_hash,
    status,
    password_changed_at,
    must_change_password)
VALUES (
    'c0000001-0000-0000-0000-000000000001',
    'admin',
    '系统管理员',
    crypt(${ADMIN_PASSWORD}, gen_salt('bf', 12)),
    'active',
    now(),
    true)
ON CONFLICT (id) DO NOTHING;

INSERT INTO user_roles (user_id, role_id, scope_type)
VALUES (
    'c0000001-0000-0000-0000-000000000001',
    'a0000001-0000-0000-0000-000000000001',
    'global')
ON CONFLICT DO NOTHING;
