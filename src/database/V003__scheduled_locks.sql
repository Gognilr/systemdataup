-- =============================================================
-- V003: 定时任务单实例锁表（设计书 §25"单实例锁"要求）
-- 背景：通知派发 / 保留策略清理等后台任务当前为进程内单实例，
--       横向扩展为多实例部署后，同一时刻只允许一个实例执行
--       每类定时任务，故引入数据库级互斥锁。
-- 机制：
--   lock_key  每类任务一个键（如 worker:notification_dispatch）
--   owner_id  持锁实例标识（机器名:进程号:作用域GUID）
--   expires_at TTL 到期后锁可被其他实例抢占（崩溃自动恢复，
--             无需人工清理；TTL 应大于任务单轮最坏耗时）
--   获取采用 INSERT ... ON CONFLICT DO UPDATE ... WHERE expires_at<=now()
--   的原子语句，并发安全；释放在任务结束时 DELETE 自己持有的行。
-- =============================================================

CREATE TABLE scheduled_locks (
    lock_key    varchar(64)  PRIMARY KEY,
    owner_id    varchar(128) NOT NULL,
    acquired_at timestamptz  NOT NULL DEFAULT now(),
    expires_at  timestamptz  NOT NULL
);

COMMENT ON TABLE scheduled_locks IS '定时任务单实例锁（多实例安全，TTL 到期自动可抢占）';
COMMENT ON COLUMN scheduled_locks.lock_key IS '任务锁键（如 worker:notification_dispatch）';
COMMENT ON COLUMN scheduled_locks.owner_id IS '持锁实例标识（机器名:进程号:作用域GUID）';
COMMENT ON COLUMN scheduled_locks.expires_at IS '锁到期时间，到期后允许其他实例抢占（崩溃恢复）';
