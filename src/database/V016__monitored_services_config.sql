-- V016：关键 Windows 服务的配置入口
--
-- monitored_service_definitions 这张表从 V001 就存在：Agent 会按它逐个探测服务状态、
-- 心跳上报、界面也有展示区块。缺的只是"谁来往里写"——没有任何接口能新增一条定义，
-- 空状态里那句「在客户端配置中添加需要监控的 Windows 服务」指向的是一个不存在的地方。
-- 这条迁移补上写入这条链路所缺的两样东西。
--
-- 一、clients.config_revision
--
-- Agent 靠心跳里的 requiredConfigVersion 判断要不要重新拉配置，而这个版本号此前
-- 完全由 backup_tasks.config_version 的最大值算出。监控服务定义虽然随配置一起下发，
-- 但改动它不会让任何任务的版本号变化——于是新增的监控服务永远到不了客户端，
-- 而且没有任何报错。客户端一个备份任务都没有时更彻底：版本恒为 0。
--
-- 加一个客户端级的修订号，由监控服务的增删改推进，与任务版本取较大者作为
-- requiredConfigVersion。两者都是单调递增的，取 max 仍然单调。
--
-- 二、command_type 增加 list_services
--
-- 让管理员从这台机器上真实存在的服务列表里挑，而不是凭记忆敲服务名。
-- MSSQLSERVER 和 MSSQL$SQLEXPRESS、SQLSERVERAGENT 和 SQLAgent$SQLEXPRESS
-- 这种名字，记错一个字符的结果是"监控了一个不存在的服务"，而它的表现是
-- 状态永远 not_found——看起来像服务挂了，实际是名字写错了。

ALTER TABLE clients
    ADD COLUMN IF NOT EXISTS config_revision bigint NOT NULL DEFAULT 0;

COMMENT ON COLUMN clients.config_revision IS
    '客户端级配置修订号；监控服务定义变更时递增，与任务版本共同决定 requiredConfigVersion';

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
        'browse_path', 'list_services'
    ));
