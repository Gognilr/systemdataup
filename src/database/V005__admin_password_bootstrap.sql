-- =============================================================
-- V005: 管理员口令引导（OPEN-ISSUES #2）
-- 背景：V001 原先硬编码默认口令 Admin@2026，任何能读到版本库的
--       人都知道初始口令；且系统没有改密接口，部署方只能手工改库。
-- 本迁移做两件事：
--   1. users 表增加 must_change_password 标志（首次登录强制改密）；
--   2. 默认管理员账户改由部署时注入：
--        psql -v admin_pw="$ADMIN_PW" -f V005__admin_password_bootstrap.sql
--      口令不再进入版本库；ON CONFLICT DO NOTHING 保证对已有库幂等。
-- 说明：:'admin_pw' 为 psql 变量，直接以 Npgsql 等方式执行本脚本
--       会报语法错误——本脚本只能经 psql 执行（dbinit.bat / 手工）。
-- =============================================================

-- 1. 增加"必须修改口令"标志（历史账户默认 false，不强制）
ALTER TABLE users ADD COLUMN must_change_password boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN users.must_change_password IS '首次登录是否强制修改口令（初始管理员为 true，改密成功后清除）';

-- 2. 防御：admin_pw 未注入/为空时显式中止，避免管理员拿到弱口令。
--    注意：psql 变量在美元引号体内不插值，故先用 SET 承接（未定义时
--    该行成语法错误，ON_ERROR_STOP 下直接中止），DO 块再读取校验。
SET app.admin_pw = :'admin_pw';

DO $$
BEGIN
    IF current_setting('app.admin_pw') IN ('admin_pw', '') THEN
        RAISE EXCEPTION 'admin_pw 未注入或为空：请以 psql -v admin_pw=''...'' 方式执行本脚本（dbinit.bat 会自动传入 %%ADMIN_PW%%）';
    END IF;
END
$$;

RESET app.admin_pw;

-- 3. 注入默认管理员（口令由 -v admin_pw 提供，bcrypt 成本 12 与登录校验一致）
INSERT INTO users (id, username, display_name, password_hash, status, password_changed_at,
                   must_change_password)
VALUES ('c0000001-0000-0000-0000-000000000001', 'admin', '系统管理员',
        crypt(:'admin_pw', gen_salt('bf', 12)), 'active', now(), true)
ON CONFLICT (id) DO NOTHING;

-- 4. 分配系统管理员角色（与账户同事务，避免外键悬空）
INSERT INTO user_roles (user_id, role_id, scope_type)
VALUES ('c0000001-0000-0000-0000-000000000001', 'a0000001-0000-0000-0000-000000000001', 'global')
ON CONFLICT DO NOTHING;
