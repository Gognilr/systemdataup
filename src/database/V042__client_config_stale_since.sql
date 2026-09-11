-- V042 客户端配置卡住的可见性（整改清单 2026-09-11 · R27）
--
-- 现场实证：一台客户端从 08:01 到 14:42 每 10 秒失败一次配置同步
-- （CONFIG_SIGNATURE_INVALID：它存的服务端签名公钥与服务端当前的对不上），
-- 整整 6.5 小时拿不到任何配置下发。而这次失败是**纯客户端侧**的——
-- 它自己拒收了服务端的响应，服务端从头到尾不知道有这回事：
-- 客户端列表里它是绿的「在线」，心跳一次没断，管理页面上没有任何提示。
--
-- 后果不是「配置晚到一会儿」：任务改了不生效、新加的任务永远下不去，
-- 而界面上一切正常。**一个不生效的配置比一个报错的配置危险得多。**
--
-- 判据不需要客户端配合：心跳里本来就带着它当前持有的配置版本号，
-- 服务端只要记下「它从什么时候开始落后于要求的版本」，超过阈值就报警。
-- 落后一小会儿是正常的（改完配置到下一次同步之间），所以记的是起点而不是布尔。
ALTER TABLE clients ADD COLUMN IF NOT EXISTS config_stale_since timestamptz NULL;

COMMENT ON COLUMN clients.config_stale_since IS
    '这台客户端从什么时候开始落后于要求的配置版本；追上就置空。超过 agent_config_stale_minutes 报警（R27）';

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
VALUES
    -- 落后多久开始报警。30 分钟：足够覆盖「改完配置等下一次同步」的正常窗口
    -- （心跳默认 60 秒），又远早于「一个上午都没下去」。
    ('agent_config_stale_minutes', '30'::jsonb, false, now())
ON CONFLICT (setting_key) DO NOTHING;
