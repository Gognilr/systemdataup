-- V038 每日摘要推送 + 未配通知渠道的提示（整改清单 2026-09-10 · R8 / R5）
--
-- 报表在此之前全是拉取式（AdminReportController 清一色 HttpGet），没有任何定时推送。
-- 对备份产品来说日报比告警更重要：**告警只在坏的时候响，日报证明这套系统本身还活着**。
-- 一个星期没收到告警，可能是一切正常，也可能是服务端三天前就死了——
-- 这两件事在收件人那里长得一模一样。

-- ── 投递记录承载非告警内容 ─────────────────────────────────────────────────
-- 日报复用 notification_deliveries 通道（不另起投递链路），但它没有对应的告警行：
-- NotificationDispatchWorker 原先一律从 delivery.Alert 组装标题和正文，
-- alert_id 为空时发出去的是一封标题「系统告警」、正文空白的邮件。
-- 这两列让投递记录可以自带内容，有就用自带的，没有就还按告警组装。
ALTER TABLE notification_deliveries
    ADD COLUMN IF NOT EXISTS subject varchar(200) NULL,
    ADD COLUMN IF NOT EXISTS body text NULL;

COMMENT ON COLUMN notification_deliveries.subject IS
    '投递自带标题；为空时按关联告警组装（日报等非告警投递使用）';
COMMENT ON COLUMN notification_deliveries.body IS
    '投递自带正文；为空时按关联告警组装';

-- ── 日报配置 ───────────────────────────────────────────────────────────────
INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
VALUES
    -- 默认开。日报的意义就是证明系统还活着，默认关掉等于这一条整改没做。
    ('daily_digest_enabled', 'true'::jsonb, false, now()),
    -- 按报表时区的本地小时发。8 点：上班坐下来第一眼能看到昨天的结果，
    -- 而不是半夜发一封到第二天已经被别的邮件压下去。
    ('daily_digest_hour', '8'::jsonb, false, now())
ON CONFLICT (setting_key) DO NOTHING;
