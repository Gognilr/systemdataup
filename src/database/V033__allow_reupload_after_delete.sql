-- =====================================================================
-- V033：删掉的备份集不该继续占着「这个候选已经入过库」的位置
--
-- uq_backup_sets_candidate 原先是无条件唯一索引，含义是「一个候选只能产生一个正式版本」。
-- 这个含义在「备份集被删掉之后」不成立：人删掉一份备份，正常预期是还能再备一次，
-- 而删除是软删（recycle_bin / deleted），行仍然在，唯一约束于是永久挡住重新入库。
--
-- 应用层有六处 `BackupSets.Any(b => b.SourceCandidateId == ...)` 同样不看状态，
-- 它们和这条约束合起来的效果是：源文件没变的那份备份，删掉之后再也备不回来，
-- 而界面上只会显示「没有新备份」。改成部分唯一索引：只有「还活着」的版本参与唯一性判定。
--
-- 口径与 Core/Enums/BackupSetStatuses.Live 必须一致，两边改一边就会以
-- 「应用层放行、数据库拒绝」的形式炸在上传入库的最后一步。
-- =====================================================================

-- 建索引之前先确认没有存量冲突数据。直接建索引失败的话，报错是一句
-- 「could not create unique index」加一个 detail，看不出该怎么办；
-- 这里主动查一遍并把处理方式写进报错里。整个迁移在一个事务内，抛出即全部回滚。
DO $$
DECLARE
    conflicts int;
    sample text;
BEGIN
    SELECT count(*), coalesce(string_agg(source_candidate_id::text, ', '), '')
    INTO conflicts, sample
    FROM (
        SELECT source_candidate_id
        FROM backup_sets
        WHERE status NOT IN ('recycle_bin', 'deleted')
        GROUP BY source_candidate_id
        HAVING count(*) > 1
        LIMIT 20
    ) AS dup;

    IF conflicts > 0 THEN
        RAISE EXCEPTION
            '存在 % 个候选在「活着」的状态下有多个备份集，无法建立部分唯一索引：%',
            conflicts, sample
        USING HINT =
            '这些数据在旧的无条件唯一约束下不可能产生，说明约束曾被绕过。'
            '请人工核对这些 source_candidate_id 下的备份集，保留一份、'
            '把多余的置为 recycle_bin 或 deleted 之后重跑迁移。';
    END IF;
END
$$;

ALTER TABLE backup_sets DROP CONSTRAINT IF EXISTS uq_backup_sets_candidate;
DROP INDEX IF EXISTS uq_backup_sets_candidate;

CREATE UNIQUE INDEX uq_backup_sets_candidate_live
    ON backup_sets (source_candidate_id)
    WHERE status NOT IN ('recycle_bin', 'deleted');

COMMENT ON INDEX uq_backup_sets_candidate_live IS
    '一个候选同时只能有一个「活着」的正式版本；回收站里的和已删除的不参与判定，'
    '否则删掉一份备份之后源文件没变就再也备不回来。';

-- =====================================================================
-- 批次三：队列与取消
-- =====================================================================

-- C6/C7：取消一次传输，必须记在「这一份备份」上，而不是只记在会话上。
--
-- 原先取消只把 upload_sessions.status 改成 cancelled。随后的自动链路会这样走：
-- CommandService 判「这个候选没有 committed、也没有 InFlight 会话」→ 结论是「上传从没落地」
-- → 指令复位重发；建会话时旧会话是 cancelled，幂等键被释放 → 建一个全新会话从 0 重传。
-- 于是「取消」的实际效果是「几分钟后从头再传一遍」。
ALTER TABLE candidate_backup_sets
    ADD COLUMN IF NOT EXISTS cancelled_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS cancelled_by UUID NULL REFERENCES users(id);

COMMENT ON COLUMN candidate_backup_sets.cancelled_at IS
    '管理员主动取消这一份的上传。取消若只落在会话上，下一轮预检会因为「没有已入库的痕迹」'
    '把上传指令复位重发，于是取消变成「几分钟后从头重传」。'
    '清除时机：源文件变了（清单哈希变化）重新预检，或管理员显式再点一次上传。';
COMMENT ON COLUMN candidate_backup_sets.cancelled_by IS '执行取消的操作人';

CREATE INDEX IF NOT EXISTS idx_candidate_backup_sets_cancelled
    ON candidate_backup_sets (task_id)
    WHERE cancelled_at IS NOT NULL;

-- C2：retry_wait 会话的回收时限。
--
-- upload_session_timeout_seconds（6 小时）问的是「一个还在传的会话多久没动静才算废了」；
-- retry_wait 保留的是断点、一路都没在传，拿 6 小时去卡它，一次失败就把这台机器锁住半天
-- ——它既占单客户端上限（默认 2）也占全局上限（默认 4），
-- 而 U8 这类一台机器 18 个账套的任务，只要两个单元失败就再也传不动第三个。
INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
VALUES ('upload_retry_wait_timeout_seconds', '1800'::jsonb, false, NULL)
ON CONFLICT (setting_key) DO NOTHING;

-- =====================================================================
-- 批次四：告警与通知链路
-- =====================================================================

-- D6：长期未恢复的高等级告警要按间隔重发通知。
--
-- 告警模型此前只有两个通知触发点：新建、等级提升。一条 Critical 挂在那里三天没人处理，
-- 系统只在第一分钟发过一封邮件——之后完全沉默，而「没有新邮件」在收件人那里
-- 读起来和「已经好了」是一样的。
ALTER TABLE alerts
    ADD COLUMN IF NOT EXISTS last_notified_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN alerts.last_notified_at IS
    '最近一次为这条告警生成通知投递的时间。距今超过 alert_renotify_hours 时重发，'
    '空值表示从未通知过（命中静默规则，或建告警时渠道尚未配置）。';

-- 已有的活动告警按「刚通知过」起算，避免升级后第一轮把存量告警全部重发一遍
UPDATE alerts
   SET last_notified_at = last_occurred_at
 WHERE last_notified_at IS NULL
   AND status IN ('open', 'acknowledged', 'in_progress');

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
VALUES ('alert_renotify_hours', '24'::jsonb, false, NULL)
ON CONFLICT (setting_key) DO NOTHING;
