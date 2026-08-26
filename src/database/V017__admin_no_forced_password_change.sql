-- =============================================================
-- V017：安装时设好的管理员口令不再强制首次改密
--
-- V005 给初始 admin 打了 must_change_password=true。这个标志针对的是「口令不是
-- 本人设的」那种引导方式（预置默认口令、随机生成后打印出来）——那时首次登录必须
-- 换掉才安全。
--
-- 但服务端安装器的实际流程里，管理员密码是操作员在安装界面上亲手输入的，而且已经
-- 过了与 Web 端自助改密同一套 PasswordPolicy 强度校验（ServerInstaller.Validate）。
-- 口令从没离开过设它的人，首次登录再逼一次改密只是多一道无收益的门槛。
--
-- 这条迁移把已装机器上的 admin 标志清掉，并把列默认值明确为 false；V005 本身不改动
-- ——它的 checksum 已经写进 schema_migrations，改脚本会让所有既有安装的迁移校验失败。
-- =============================================================

ALTER TABLE users
    ALTER COLUMN must_change_password SET DEFAULT false;

COMMENT ON COLUMN users.must_change_password IS
    '首次登录是否强制修改口令。安装器设置的初始管理员口令由操作员亲自输入并已过强度校验，因此不置位；仅在口令由系统代设时才置 true，改密成功后清除。';

UPDATE users
SET must_change_password = false,
    updated_at = now()
WHERE username = 'admin'
  AND must_change_password;
