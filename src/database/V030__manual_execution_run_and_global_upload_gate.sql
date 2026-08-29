-- =====================================================================
-- V030：手动「立即备份」接进执行队列 + 全局上传并发闸（批次 D3）
--
-- 在此之前，手动立即备份两套并发闸一套都不走：计划执行有 execution_runs.max_concurrent，
-- 批量操作有 upload_batches.max_concurrent_clients，而 BackupTaskService 的
-- DispatchPrecheckAsync / DispatchUploadAsync 是直接 CreateCommandAsync。
-- 在任务列表里连点十个任务的「立即备份」，十个会同时开始传，服务端暂存盘扛不住。
--
-- 唯一与上传并发有关的检查是按客户端的（upload_sessions.client_id = ...），
-- 它管的是「一台客户端最多同时传几个」，管不了「服务端同时被几台客户端传」。
--
-- 两件事：
--   一、execution_run_kind 加 'manual'：多选批量「立即备份」建一次执行，
--       并发度取系统设置，而不是发 N 条独立指令；
--   二、新增 max_concurrent_uploads_total：全局活动上传会话上限，由
--       SequentialExecutionWorker 在放行下一项之前统一检查。它不只管手动点击——
--       计划执行、批量操作、自动模式下预检通过后的自动上传全部受同一个上限约束。
--       只堵手动这一条路，换个入口照样能把盘打满。
--
-- 闸放在下发侧而不是建会话侧：Agent 对 UPLOAD_SESSION_CONFLICT 没有退避重试，
-- 在建会话这一步 409 等于把限流变成备份失败。因此 upload_sessions 那条
-- 按客户端的检查保持不动——它是兜底，与队列职责不同。
--
-- 默认值 4 是个起点，取决于服务端暂存盘的实际吞吐（机械盘 / SSD / 阵列差别很大），
-- 上线后按实测的拐点调。
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. execution_run_kind 增加 manual
-- ---------------------------------------------------------------------

DO $$
DECLARE
    constraint_name text;
BEGIN
    SELECT con.conname INTO constraint_name
      FROM pg_constraint con
      JOIN pg_type typ ON typ.oid = con.contypid
     WHERE typ.typname = 'execution_run_kind'
       AND con.contype = 'c'
     LIMIT 1;

    IF constraint_name IS NOT NULL THEN
        EXECUTE format('ALTER DOMAIN execution_run_kind DROP CONSTRAINT %I', constraint_name);
    END IF;
END
$$;

ALTER DOMAIN execution_run_kind ADD CONSTRAINT execution_run_kind_check
    CHECK (VALUE IN ('backup_plan', 'upload_batch', 'manual'));

-- ---------------------------------------------------------------------
-- 2. 全局上传并发上限
-- ---------------------------------------------------------------------

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
VALUES ('max_concurrent_uploads_total', '4'::jsonb, false, NULL)
ON CONFLICT (setting_key) DO NOTHING;

-- 手动执行的并发度：批量「立即备份」建出来的 execution_run 取这个值当 max_concurrent。
-- 与全局闸是两码事——它管「这一批同时放行几项」，全局闸管「整个服务端同时传几个」。
INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
VALUES ('manual_run_max_concurrent', '2'::jsonb, false, NULL)
ON CONFLICT (setting_key) DO NOTHING;
