-- V037 「可疑」时收下并标记 + 历史基线大小判据（整改清单 2026-09-10 · R15 / R16）
--
-- 在此之前，大小判定的结果是**拒收**：ValidateSizeThresholds 判出 SizeAbnormal，
-- 候选的 precheck_status 就停在非 passed，于是 UploadSessionService 抛 409、
-- BatchOperationService 直接跳过。也就是说「今天的备份只有平时 10% 大小」时，
-- 系统的反应是「一个字节都不收」。
--
-- 一份可疑的备份，比没有备份好。判断权交给人，不要替人决定这份不要了。
-- 改成：照常入库 → 打「大小可疑」标记 → 保留 size_abnormal 告警。
--
-- 顺带消掉一个连带效果：被拒收的那一天 last_success_at 不更新，过了宽限期
-- MissedBackupWorker 还会再报一条 backup_missed——同一件事报两条，
-- 而且其中一条把人引向错误方向（备份其实产生了，是被系统拒收了）。

-- ── R15：可疑标记 ───────────────────────────────────────────────────────────
ALTER TABLE candidate_backup_sets
    ADD COLUMN IF NOT EXISTS size_suspicious boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS size_suspicion_reason text NULL;

COMMENT ON COLUMN candidate_backup_sets.size_suspicious IS
    '大小/文件数可疑：仍然照收入库，只是标记出来交给人判断';

-- 标记要跟着备份集走。人是在备份列表和详情页上看这件事的，
-- 而候选在入库之后就不再是使用者会打开的东西了。
ALTER TABLE backup_sets
    ADD COLUMN IF NOT EXISTS size_suspicious boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS size_suspicion_reason text NULL;

COMMENT ON COLUMN backup_sets.size_suspicious IS
    '入库时大小/文件数可疑；备份本身仍然可用，这是一个「请人看一眼」的标记';

-- 备份列表要能筛「只看可疑的」，而可疑的是极少数——部分索引正合适。
CREATE INDEX IF NOT EXISTS idx_backup_sets_size_suspicious
    ON backup_sets (task_id, uploaded_at DESC)
    WHERE size_suspicious;

-- ── R16：历史基线参数 ───────────────────────────────────────────────────────
-- 固定阈值（min_total_bytes 等）保留为硬下限：它们抓的是「绝对不该低于这个数」。
-- 但固定死数抓不到最有价值的那个信号——「今天这份比过去 N 次的中位数小了 90%」。
-- 结构快检（原 R14）已决定不做，这一条现在是「备份跑了但是空的 / 只剩十分之一」的唯一兜底。
INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
VALUES
    -- 样本不足这个数就不判。新任务不该因为没有历史就报警
    -- （与漏备份巡检「刚建出来的任务不算漏」同一条原则）。
    ('size_baseline_sample_count', '10'::jsonb, false, now()),
    -- 总字节数低于中位数的这个比例即可疑。0.5 是刻意给宽的：
    -- 这条判据的价值在于抓「只剩十分之一」，不在于抓百分之十的波动。
    ('size_baseline_min_bytes_ratio', '0.5'::jsonb, false, now()),
    -- 文件数用**宽带宽**：U8 附件库个数随启用年度不同，个数波动是常态，
    -- 不能拿它当缺失判据。真正敏感的是总字节数。0.2 = 掉到平时的五分之一才说话。
    ('size_baseline_min_file_ratio', '0.2'::jsonb, false, now())
ON CONFLICT (setting_key) DO NOTHING;
