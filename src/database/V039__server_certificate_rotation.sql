-- V039 服务端 TLS 证书指纹轮换的过渡期（整改清单 2026-09-10 · R9 第 2 点）
--
-- 现状是一个死结：Agent 按指纹固定连服务端，而「重新签发服务端证书」会改变指纹，
-- 于是全网 Agent 立刻连不上；而它们连不上，就收不到新指纹——
-- 唯一的出路是挨台机器重跑客户端安装器。
-- 证书只有 2 年有效期，这件事迟早要做一次，而它现在做不了。
--
-- 解开这个结的办法是让 Agent 在一段时间内**同时接受两个指纹**，
-- 并且新指纹要在旧证书还能用的时候就送到它们手里。过渡流程：
--
--   1. 服务管理台「预备新的服务端证书」：生成 server-certificate.next.pfx 但**不启用**，
--      把它的指纹写进 server_certificate_next_fingerprint。此时对外仍然用旧证书。
--   2. 服务端把这个指纹随签名配置下发给全部 Agent。Agent 把它存进 state.json，
--      从此同时接受新旧两个指纹，并在心跳里回报自己已经持有它。
--   3. 管理台显示「N / M 台客户端已就绪」。等到全部就绪（或管理员确认剩下那几台
--      本来就下线了）再走第 4 步。
--   4. 服务管理台「启用新的服务端证书」：next.pfx 顶替当前证书并重启服务。
--      Agent 用第二个指纹接上，全程无需上门。
--   5. 全部 Agent 都用新指纹连上之后，清掉过渡配置，回到单指纹状态。
--
-- 第 3 步是这套流程的价值所在：没有它，第 4 步就是一次闭着眼睛的赌博。

-- 客户端回报自己已经接受的「预备指纹」。为空 = 还没拿到，那台机器在第 4 步会掉线。
ALTER TABLE clients
    ADD COLUMN IF NOT EXISTS accepted_next_server_fingerprint varchar(128) NULL;

COMMENT ON COLUMN clients.accepted_next_server_fingerprint IS
    '这台 Agent 已接受的预备服务端指纹（R9 过渡期）；与当前预备指纹一致才算就绪';

-- 过渡期的两个配置键。它们都不是机密：证书指纹在每一次 TLS 握手里都会公开出示。
INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
VALUES
    -- 预备（尚未启用）的服务端证书指纹。为空表示当前不在过渡期。
    ('server_certificate_next_fingerprint', '""'::jsonb, false, now()),
    -- 上一版服务端证书的指纹。启用新证书之后短期保留，让还没来得及重连的 Agent
    -- 仍然认得出旧的那一张——**方向反过来的兼容**，第 4 步之后才有意义。
    ('server_certificate_previous_fingerprint', '""'::jsonb, false, now())
ON CONFLICT (setting_key) DO NOTHING;
