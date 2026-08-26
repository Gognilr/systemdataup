-- =====================================================================
-- V019：整改批次 C（安全）—— C1 历史审计脱敏 / C4 令牌版本号
--
-- C1（SMTP / webhook 凭据明文存储、明文回显、明文进审计日志）：
--   凭据加密改由应用层（BackupMonitor.Infrastructure.Security.SecretProtector）负责，
--   这里只清理已经写进 audit_logs.after_data 的历史明文凭据。这些凭据一旦进过审计日志
--   即视为已泄露，本迁移只负责脱敏审计记录本身——运维仍必须重置 SMTP 密码、
--   重新生成企业微信/钉钉机器人 webhook（access_token 本身就是凭据），不能只靠加密补救。
--
-- C4（权限烤死在令牌里，撤权最长 1 小时后才生效）：
--   users 表加 token_version，签发 JWT 时带 tv claim；改密/登出（以及未来实现的
--   禁用账号/改角色/改权限）时 ++；服务端用 60 秒缓存比对，不一致即拒绝。
--
-- 全部幂等：可重复执行，不会重复清空已脱敏的审计记录或破坏已存在的列。
-- =====================================================================

-- C4：令牌版本号，默认 0（新建/未改密用户的初始版本）
ALTER TABLE users ADD COLUMN IF NOT EXISTS token_version int NOT NULL DEFAULT 0;

COMMENT ON COLUMN users.token_version IS
    '令牌版本号（整改批次 C · C4）。改密/登出/禁用账号/改角色改权限时 ++；与访问令牌 tv claim 比对（60 秒缓存），不一致即视为令牌已撤销。';

-- C1：清理历史审计日志中的整包凭据（notification.update_settings 此前把
-- 含 smtpPassword / webhookUrl 的完整请求体序列化进了 after_data）
UPDATE audit_logs
SET after_data = '{"redacted":true,"reason":"C1 remediation: historical plaintext credentials scrubbed"}'::jsonb
WHERE action = 'notification.update_settings'
  AND after_data IS NOT NULL
  AND after_data->>'redacted' IS DISTINCT FROM 'true';
