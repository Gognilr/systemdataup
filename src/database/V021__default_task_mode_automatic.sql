-- =============================================================
-- V021：任务默认模式改为「自动」，并把卡死的既有任务迁过去
--
-- V001 把 backup_tasks.task_mode 的默认值定为 'approval_required'。含义是：客户端扫到
-- 一份新备份、预检通过之后，不自动上传，等人在管理端逐份确认。
--
-- 问题在于「确认」这一步从来没有界面。AgentPrecheckService 会把候选备份集置为
-- wait_for_approval，而管理端唯一能下发上传的入口，是客户端列表里让操作员手工粘贴
-- 候选备份集 GUID 的批量上传弹窗——GUID 本身又没有任何列表端点可查。也就是说，
-- 按默认值建出来的任务会静静地扫描、静静地通过预检，然后永远不上传，界面上也不会
-- 提示他在等什么。
--
-- 因此：
--   1) 列默认值改为 'automatic'（预检通过即下发上传指令）；
--   2) 既有的 approval_required 任务一并迁到 automatic——它们在当前版本里没有任何
--      可用的放行路径，留着就是一直不备份。暂停中任务的 previous_task_mode 同样处理，
--      否则「恢复」会把它送回同一个死胡同。
--
-- 仍然需要人工把关的场景，改用 monitor_only（只扫不传、只告警）或 manual，
-- 这两个模式是明确选出来的，不会因为没改默认值而误入。
--
-- 若某个部署确实靠调 API 走审批流，跳过下面两条 UPDATE，只保留 ALTER 即可。
-- =============================================================

ALTER TABLE backup_tasks
    ALTER COLUMN task_mode SET DEFAULT 'automatic';

COMMENT ON COLUMN backup_tasks.task_mode IS
    '任务模式：automatic 预检通过即自动上传（默认）；approval_required 等人工下发上传；manual 只人工触发；monitor_only 只扫描告警不上传；paused 暂停调度。';

UPDATE backup_tasks
   SET task_mode   = 'automatic',
       config_version = config_version + 1,
       updated_at  = now(),
       row_version = row_version + 1
 WHERE task_mode = 'approval_required';

UPDATE backup_tasks
   SET previous_task_mode = 'automatic',
       updated_at         = now(),
       row_version        = row_version + 1
 WHERE previous_task_mode = 'approval_required';
