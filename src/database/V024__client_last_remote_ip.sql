-- V024：客户端 IP 可见性
--
-- 原先 clients.ip_addresses 只在注册那一刻写入一次（AgentRegistrationService），
-- 之后永不刷新：DHCP 续租、换网段、插新网卡之后，库里那份就永久过期，
-- 而界面上看不出它是旧的。现在心跳会在网卡列表变化时刷新它。
--
-- last_remote_ip 是另一个维度：服务端在 TCP 层实际看到的对端地址
-- （UseForwardedHeaders 之后即 nginx 后面的真实客户端 IP）。
-- 它只有一个值、每次心跳都刷新、且必然是真正连得通的那个地址——
-- 多网卡机器上 Agent 报的那一串里到底哪个在用，只有它能回答。
ALTER TABLE clients ADD COLUMN IF NOT EXISTS last_remote_ip varchar(64);

COMMENT ON COLUMN clients.last_remote_ip IS '服务端最近一次心跳观测到的对端 IP（经 X-Forwarded-For 还原），每次心跳刷新';
COMMENT ON COLUMN clients.ip_addresses IS 'Agent 自报的本机网卡地址列表（jsonb 字符串数组），仅在列表发生变化的那次心跳上报';
