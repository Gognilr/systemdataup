-- =====================================================================
-- V027：让保留清理真的会清理
--
-- 现状是「配了保留策略这套东西，但一份备份也不会被清掉」，三个原因叠在一起：
--
--   1) RetentionCleanupWorker 只处理 retention_policy_id 非空的任务，
--      而任务表单里「保留策略」在高级折叠区、默认留空 —— 建出来的任务全部不清理，
--      仓库无限增长。「留空 = 永不清理」这个默认值是反的。
--   2) minimum_retention_days 默认 30，语义是「不满 30 天一律不回收」，
--      会直接压过「只留最近 N 份」。每天备份的话实际会攒到 30 份以上。
--   3) 唯一的种子策略是一份 GFS（8 周 + 6 月 + 2 年），对「我就想留最近几份」
--      这个绝大多数人的诉求属于过度设计，没人会去建策略。
--
-- 因此：
--   * 两个天数默认值改成「最短保留 0 天 / 回收区 7 天」——份数说了算，
--     删掉的先在回收站放一周还能捞回来；
--   * 预置三份开箱即用的策略，任务表单直接选；
--   * 用 system_settings.default_retention_policy_id 指定「新建任务的默认策略」，
--     服务端在建任务时自动绑定（BackupTaskService.CreateAsync）；
--   * 把既有的未绑定任务一并绑到默认策略上——它们正是「永远不清理」的那批。
--     回收要先经过回收站（7 天）且受回收熔断保护，不会直接物理删除。
-- =====================================================================

ALTER TABLE retention_policies ALTER COLUMN minimum_retention_days SET DEFAULT 0;
ALTER TABLE retention_policies ALTER COLUMN recycle_bin_days       SET DEFAULT 7;

COMMENT ON COLUMN retention_policies.minimum_retention_days IS
    '最短保留天数：不满该天数的备份集一律不回收，会覆盖「保留最近 N 份」。默认 0 表示只按份数/周期规则算。';

-- 原来的种子策略同样受 minimum_retention_days=30 拖累，一并放开
UPDATE retention_policies
   SET minimum_retention_days = 0,
       updated_at = now()
 WHERE id = 'd0000001-0000-0000-0000-000000000001'
   AND minimum_retention_days = 30;

-- 预置策略：装机即可用，不用自己建
INSERT INTO retention_policies
    (id, name, keep_last_count, keep_weekly_count, keep_monthly_count, keep_yearly_count,
     minimum_retention_days, recycle_bin_days)
VALUES
    ('d0000002-0000-0000-0000-000000000003', '只留最近 3 份',
     3,    NULL, NULL, NULL, 0, 7),
    ('d0000002-0000-0000-0000-000000000007', '只留最近 7 份',
     7,    NULL, NULL, NULL, 0, 7),
    ('d0000002-0000-0000-0000-000000000012', '日备留 7 天 + 月末留 12 个月',
     7,    NULL, 12,   NULL, 0, 7)
ON CONFLICT (id) DO NOTHING;

-- 新建任务的默认策略。留空字符串表示「不自动绑定」（回到旧行为）。
INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
VALUES ('default_retention_policy_id', '"d0000002-0000-0000-0000-000000000007"'::jsonb, false, NULL)
ON CONFLICT (setting_key) DO NOTHING;

-- 既有的未绑定任务：绑上默认策略，否则它们永远不会被清理
UPDATE backup_tasks
   SET retention_policy_id = 'd0000002-0000-0000-0000-000000000007',
       updated_at  = now(),
       row_version = row_version + 1
 WHERE retention_policy_id IS NULL;
