-- =====================================================================
-- V011：备份集编码改用数据库序列（审查 P2-7）
--
-- 原实现用 COUNT(*) + 1 生成 BS-yyyy-NNNN：保留策略物理删除掉 BS-2026-0005 之后
-- 计数回落，下一个新备份集会重新拿到这个编码。该编码出现在 manifest.json、
-- 审计日志与告警正文里，复用会直接破坏可追溯性——两份不同的数据用同一个名字。
--
-- 序列单调递增、不随删除回落，且并发下由数据库保证唯一，不需要应用层重试。
-- 按年分段的语义（BS-yyyy-NNNN）保留：年份取自应用层，序号取自序列。
-- =====================================================================

CREATE SEQUENCE IF NOT EXISTS backup_set_code_seq AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE;

-- 已有数据迁移：把序列推进到「现存编码的最大序号」之后，避免与历史编码撞号。
-- 只认 BS-<4位年>-<数字> 这种规范形态，历史上的 Guid 回退编码（BS-2026-a1b2c3d4）跳过。
SELECT setval(
    'backup_set_code_seq',
    GREATEST(
        (
            SELECT COALESCE(MAX(SUBSTRING(backup_set_code FROM '^BS-[0-9]{4}-([0-9]+)$')::bigint), 0)
            FROM backup_sets
            WHERE backup_set_code ~ '^BS-[0-9]{4}-[0-9]+$'
        ),
        1
    ),
    true
);

COMMENT ON SEQUENCE backup_set_code_seq IS
    '备份集编码序号来源（V011，审查 P2-7）。单调递增，删除后不回落，保证编码永不复用。';
