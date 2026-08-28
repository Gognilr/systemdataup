-- =====================================================================
-- V029：上传会话超时放宽（待办方案 E 配套）
--
-- upload_session_timeout_seconds 原来是 3600——会话一小时没有分块写入就判过期。
-- 对「传一份 6GB 的备份集」这件事来说这个窗口太窄了：链路慢、白天限速、
-- 或者人为暂停一会儿，都会让它在还能续传的时候先被判死。
-- 会话被判过期之后暂存会被清，下次只能从 0 开始——正是这一项要消灭的行为。
--
-- 改成 6 小时。它不是「一次上传最多能传多久」，而是「多久没有任何动静才认定它废了」，
-- 放宽的代价只是废会话在库里多留一会儿，而收窄的代价是真的重传一遍。
--
-- 暂停中的会话由 LifecycleExpiryWorker 按同一条超时线判定：暂停超过 6 小时
-- 仍然会被回收，避免有人点了暂停之后忘掉，让暂存空间被永久占住。
-- =====================================================================

UPDATE system_settings
   SET setting_value = '21600'::jsonb,
       updated_at    = now()
 WHERE setting_key = 'upload_session_timeout_seconds'
   AND setting_value = '3600'::jsonb;

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
VALUES ('upload_session_timeout_seconds', '21600'::jsonb, false, NULL)
ON CONFLICT (setting_key) DO NOTHING;
