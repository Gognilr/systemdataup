-- V045 备份计划完成回执
--
-- 按任务发成功回执（V044）解决了「今天这个任务做成没有」，但代价是条数：
-- 一个 12 项的计划，一晚上就是 12 条「都挺好」。而计划本来就是一个整体——
-- 人真正要问的是「昨晚那批跑完了吗，有没有翻车的」，不是逐个任务的流水账。
--
-- execution_runs 里 total_items / succeeded_items / failed_items 是现成的，
-- SequentialExecutionWorker.FinalizeRunAsync 是这一批的唯一终点，
-- 一次计划跑完只会走一次。所以这条回执一晚上只发一条，还能把失败的项列出来。
--
-- 两条刻意的设计：
-- 1. **跑完就发，不只在有失败时发**。只在出事时发的摘要会退化成另一种告警，
--    而它真正不可替代的价值恰恰是那条「12 项全成功」——收到它才知道计划确实跑了。
-- 2. **只对计划到点触发的执行发**。人手点「立即执行」时正盯着页面看，
--    再推一条到群里是纯噪音（trigger_type 区分得开）。
ALTER TABLE backup_plans ADD COLUMN IF NOT EXISTS notify_on_finish boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN backup_plans.notify_on_finish IS
    '这个计划每次到点跑完后发一条汇总回执（成功几项、失败几项、哪几项失败）；默认关闭';
