-- =====================================================================
-- V032：自动登记的确认标记
--
-- 待办页的「最近自动登记」此前是一个纯时间窗口（7 天内 lan_simple 登记的客户端）：
-- 人点了「查看」、确认过这台机器是自己装的，条目照样留在队列里，
-- 直到第 7 天自己消失。于是这一类事项永远无法「处理完」——
-- 而待办页对使用者的承诺恰恰是「处理后会从队列移除」。
--
-- 队列里长期挂着几条清不掉的东西，代价不是碍眼：人会开始不看这个页面，
-- 真正需要处理的（待审批、严重告警）也一起被无视。
--
-- 所以给「我看过了，这台机器是预期内的」一个落点。确认只影响待办队列，
-- 不改变客户端状态、不影响任何备份链路。
-- =====================================================================

ALTER TABLE clients
    ADD COLUMN IF NOT EXISTS enrollment_reviewed_at timestamptz,
    ADD COLUMN IF NOT EXISTS enrollment_reviewed_by UUID REFERENCES users(id);

COMMENT ON COLUMN clients.enrollment_reviewed_at IS '自动登记已被人工确认的时间；非空表示不再出现在待办队列';
COMMENT ON COLUMN clients.enrollment_reviewed_by IS '确认这次自动登记的操作人';
