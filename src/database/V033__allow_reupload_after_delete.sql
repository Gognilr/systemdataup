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
