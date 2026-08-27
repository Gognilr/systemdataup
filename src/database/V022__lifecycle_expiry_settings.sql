-- =====================================================================
-- V022：生命周期到期巡检相关系统配置（审计批次 A：A-03 / A-04，批次 D：D-05）
--
-- upload_sessions.expired 与 commands.expired 这两格状态在 V001 建表时就定义了，
-- 但全代码库零处写入——「时间推动状态转移」这件事一直没有执行者。
-- LifecycleExpiryWorker 补上这个执行者，这里补上它要读的配置项。
--
-- 超时阈值键 upload_session_timeout_seconds 在 V001 里已经种好
-- （此前被取出来后从未使用），本迁移不重复插入。
--
-- command_claim_timeout_seconds 属于批次 D，提前一起种下：两批共用同一个
-- worker，没必要为一个键再开一次迁移。
--
-- 全部幂等：已存在的键不覆盖，管理员改过的值不会被回滚。
-- =====================================================================

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by) VALUES
    -- A-03：LifecycleExpiryWorker 的巡检间隔（秒）。会话超时默认 3600 秒，
    --       5 分钟一轮带来的判定延迟相对阈值可以忽略，而单轮开销主要在删目录。
    ('lifecycle_expiry_interval_seconds', '300'::jsonb, false, NULL),

    -- A-04：终态会话暂存目录的保留期（小时）。不是立刻删——失败的会话现场
    --       是排障时唯一能看的东西，留一天的窗口。
    ('staging_cleanup_retention_hours',   '24'::jsonb,  false, NULL),

    -- D-05：指令被领取后多久未上报开始就退回 pending（秒）。
    --       断线重连、机器重启后应该继续干活，而不是干等指令 TTL 熬完。
    ('command_claim_timeout_seconds',     '900'::jsonb, false, NULL)
ON CONFLICT (setting_key) DO NOTHING;
