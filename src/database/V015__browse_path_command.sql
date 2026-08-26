-- V015：新增 browse_path 指令类型
--
-- 建任务向导需要让管理员在界面上"打开"客户端的目录来选备份路径，而不是靠记忆
-- 把路径敲进输入框、再靠理解把目录结构翻译成识别规则 JSON。承载这件事的是一条
-- 新指令：browse_path —— Agent 抓一棵只含元数据的目录树回来，服务端据此推断
-- 识别规则并在界面上预演判定结果。
--
-- command_type 是带 CHECK 的 DOMAIN 而不是 PG 原生 enum，所以扩充要先删约束再重建。
-- 约束名按 PostgreSQL 的默认命名是 command_type_check，但为了不依赖这个默认值，
-- 这里从 pg_constraint 里查出实际名字再删——迁移脚本失败在半途比多写五行难收拾得多。

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
        'browse_path'
    ));
