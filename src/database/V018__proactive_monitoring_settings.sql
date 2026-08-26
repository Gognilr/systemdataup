-- =====================================================================
-- V018：主动巡检相关系统配置（整改批次 A：A1 / A2 / A4）
--
-- 系统原有的告警触发点全部挂在「Agent 报上来了什么」之后，于是最该报警的三件事
-- ——机器掉线、备份没做、服务端磁盘要满——恰好都属于「什么都没发生」，永远发不出来。
-- 批次 A 引入两个主动巡检 worker，这里补上它们要读的配置项。
--
-- 阈值键 client_offline_threshold_seconds / client_suspected_offline_seconds
-- 在 V001 里已经种好（此前在 C# 侧零引用），本迁移不重复插入。
--
-- 全部幂等：已存在的键不覆盖，管理员改过的值不会被回滚。
-- =====================================================================

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by) VALUES
    -- A1：SystemWatchdogWorker 的巡检间隔（秒）。判定本身很轻（几十到几百行客户端），
    --     间隔取心跳间隔的一半量级，保证 300 秒离线阈值的判定延迟可以忽略。
    ('client_liveness_interval_seconds',    '30'::jsonb,  false, NULL),

    -- A2：MissedBackupWorker 的巡检间隔（分钟）。
    ('missed_backup_scan_interval_minutes', '15'::jsonb,  false, NULL),

    -- A2：漏备份宽限期（分钟）。计划时刻起多久仍无成功入库才算漏备份；
    --     同时也是「分钟级 cron 不报漏备份」的判据（相邻两次计划间隔小于它的任务跳过）。
    ('missed_backup_grace_minutes',         '120'::jsonb, false, NULL),

    -- A4：服务端仓库/暂存磁盘可用比例告警阈值（百分比）。低于 5% 时告警自动升为严重。
    ('server_storage_alert_percent',        '15'::jsonb,  false, NULL)
ON CONFLICT (setting_key) DO NOTHING;
