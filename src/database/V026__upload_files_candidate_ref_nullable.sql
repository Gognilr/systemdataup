-- =====================================================================
-- V026：upload_files.candidate_file_id 改为可空 + ON DELETE SET NULL
--
-- 症状：任何一个曾经开始上传过的任务，之后再也预检不了——不管是 cron 到点自动扫，
-- 还是人在界面上点「下发预检」，接口一律 500「服务器内部错误，请稍后重试」，
-- 告警中心里堆的全是「precheck_task 指令执行失败」。任务就此永久卡死。
--
-- 成因：预检通过时服务端要用新清单整体替换 candidate_files
-- （AgentPrecheckService.SubmitResultAsync），而 upload_files.candidate_file_id
-- 以 NOT NULL + 默认 NO ACTION 引用着这些行。只要那个候选建过一次上传会话——
-- 哪怕会话立刻被取消、一个字节都没传完——upload_files 里就留下了引用，
-- DELETE FROM candidate_files 撞 23503，整个预检请求回滚。
--
-- 为什么是放开引用而不是保住它：upload_files 自带 relative_path、size_bytes、
-- expected_sha256、temp_path，上传与校验全程不读 candidate_file_id——
-- 全代码库只有一处写它，没有任何一处查它。为一个从不被读的回指字段，
-- 让「同一份备份不能扫第二次」，这笔账怎么算都不对。
--
-- 幂等：约束名不存在时不报错，重复执行无副作用。
-- =====================================================================

ALTER TABLE upload_files ALTER COLUMN candidate_file_id DROP NOT NULL;

ALTER TABLE upload_files DROP CONSTRAINT IF EXISTS upload_files_candidate_file_id_fkey;

ALTER TABLE upload_files
    ADD CONSTRAINT upload_files_candidate_file_id_fkey
    FOREIGN KEY (candidate_file_id) REFERENCES candidate_files(id) ON DELETE SET NULL;
