-- V034：客户端存活判定改用「最近一次通信」，不再只认心跳
--
-- 原先 last_heartbeat_at 是唯一的存活证据，而全系统只有 POST /api/v1/agent/heartbeat
-- 这一个端点会写它。于是存活判定与事实脱节，两个方向都错：
--
--   一、一台正在传 5 GB 备份的客户端，每几秒就要跟服务端说一次话（领指令、报扫描进度、
--       传分块），它显然活着；但只要心跳线程被大文件哈希拖住或那一两次心跳丢包，
--       它照样会在 180 秒后变「疑似离线」、300 秒后变「离线」并发严重告警。
--       告警响的时候它正在好好地备份——这种告警响几次之后就没人看了。
--   二、反过来，判定完全依赖一个固定时钟：要知道一台机器是不是真的没了，
--       只能等满阈值，没有任何更直接的证据可用。
--
-- last_seen_at 记的是「服务端最近一次收到这个客户端的任何一个已认证请求」，
-- 由 ClientLastSeenMiddleware 写入（同一客户端最多每 15 秒落一次库，
-- 分块上传那种高频请求不会把这张表写爆）。存活判定取
-- greatest(last_heartbeat_at, last_seen_at)：任何一次通信都是比心跳更强的存活证据。
--
-- 两列都保留、都显示：心跳带着系统信息与配置版本，是「它在按约定汇报」；
-- last_seen_at 只回答「它还在不在」。把后者并进前者会让心跳这个词失去意义。
ALTER TABLE clients ADD COLUMN IF NOT EXISTS last_seen_at timestamptz;

-- 存量客户端先按心跳时间兜底，避免升级后的第一轮巡检把全部客户端判成从未见过。
UPDATE clients SET last_seen_at = last_heartbeat_at WHERE last_seen_at IS NULL;

COMMENT ON COLUMN clients.last_seen_at IS '服务端最近一次收到该客户端任何已认证请求的时间；存活判定取它与 last_heartbeat_at 的较晚者';
