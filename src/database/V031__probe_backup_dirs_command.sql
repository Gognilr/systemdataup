-- =====================================================================
-- V031：新增 probe_backup_dirs 指令类型（批次 B7）
--
-- 「这台机器上还有没有别的备份没被监控起来」——这个问题此前无法回答。
-- 向导的起点是盘符列表，之后只能人工一层层点进去找。
--
-- 这不是缺陷，是能力缺失：现有的 browse_path 是**定向**的（给一个路径，返回那个
-- 路径下的树），没有「给一台机器，返回像备份目录的地方」这种形态。
--
-- 新指令的三条边界与 browse_path 完全一致，且更严：
--   · 只返回候选目录本身的统计特征，**不返回任何文件名**；
--   · 条目数 / 深度 / 耗时三重硬上限，触顶如实标记并把已算出的候选原样返回；
--   · 拒绝访问要说出来，不能表现成「这里没有备份」。
--
-- command_type 是带 CHECK 的 DOMAIN 而不是 PG 原生 enum，扩充要先删约束再重建。
-- 约束名从 pg_constraint 查出实际值再删，不依赖 PostgreSQL 的默认命名（同 V015）。
-- =====================================================================

DO $$
DECLARE
    constraint_name text;
BEGIN
    SELECT con.conname INTO constraint_name
      FROM pg_constraint con
      JOIN pg_type typ ON typ.oid = con.contypid
     WHERE typ.typname = 'command_type'
       AND con.contype = 'c'
     LIMIT 1;

    IF constraint_name IS NOT NULL THEN
        EXECUTE format('ALTER DOMAIN command_type DROP CONSTRAINT %I', constraint_name);
    END IF;
END
$$;

ALTER DOMAIN command_type ADD CONSTRAINT command_type_check
    CHECK (VALUE IN (
        'precheck_task', 'precheck_all',
        'upload_candidate', 'upload_latest',
        'pause_upload', 'resume_upload', 'cancel_upload',
        'rescan', 'rehash', 'sync_config',
        'refresh_metrics', 'upgrade_agent',
        'browse_path', 'list_services',
        'probe_backup_dirs'
    ));
