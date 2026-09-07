-- V035 传输性能调优（实施方案 2026-09-05 · T2 / T7）
--
-- 两件事：
--   1. T2：整文件哈希复核从 complete-file 的同步请求路径挪到入库阶段。
--      客户端在 complete 时声明的哈希此后没有地方比了，存下来，入库时一并交叉验证。
--   2. T7：三个传输并发度参数改由服务端随任务下发，不再是客户端本地的 appsettings 值。
--      它们是**性能参数**，不是限速手段——限速的唯一口径仍然是 bandwidth_limit_kbps。

-- ── T2 ──────────────────────────────────────────────────────────────────────
-- 客户端在 POST .../complete 时自报的整文件 SHA-256。
-- 权威基准仍然是 expected_sha256（来自预检清单，服务端持有）；这一列只作附加交叉验证，
-- 它由上传方单方面给出，任何时候都不能拿它当唯一判据。
ALTER TABLE upload_files
    ADD COLUMN IF NOT EXISTS client_declared_sha256 varchar(64) NULL;

COMMENT ON COLUMN upload_files.client_declared_sha256 IS
    '客户端 complete 时自报的整文件 SHA-256；仅作交叉验证，权威基准是 expected_sha256';

-- ── T7 ──────────────────────────────────────────────────────────────────────
-- 单文件内同时在途的分块数。默认 4：一块的周期里真正占用网络的只有传输那一小段，
-- 串行时链路利用率只有个位数百分比；再往上收益递减而内存与服务端压力线性涨。
ALTER TABLE backup_tasks
    ADD COLUMN IF NOT EXISTS max_parallel_chunks integer NOT NULL DEFAULT 4;

-- 同时在传的文件数。默认 3：解决的是「每个文件三次串行往返」，
-- 对几千个小文件的附件库形态收益最大；它不会放大在途分块数（两者共用同一个信号量）。
ALTER TABLE backup_tasks
    ADD COLUMN IF NOT EXISTS max_parallel_files integer NOT NULL DEFAULT 3;

-- 预检阶段同时读盘算 SHA-256 的文件数。默认 3：
-- 单流顺序读吃不满盘，但并发太多会让机械盘退化成随机读、比串行更慢。
ALTER TABLE backup_tasks
    ADD COLUMN IF NOT EXISTS max_parallel_hashes integer NOT NULL DEFAULT 3;

-- 范围约束写进库里，而不只写在 BackupTaskService 里：
-- 直接改库塞进 0 或 999 的情况必须被挡住，否则客户端只能靠自己夹回来，
-- 而「限速/并发配错」的表现是「传得莫名其妙地慢」——最不容易被发现的那种故障。
ALTER TABLE backup_tasks
    DROP CONSTRAINT IF EXISTS ck_backup_tasks_parallelism;
ALTER TABLE backup_tasks
    ADD CONSTRAINT ck_backup_tasks_parallelism CHECK (
        max_parallel_chunks BETWEEN 1 AND 16
        AND max_parallel_files BETWEEN 1 AND 8
        AND max_parallel_hashes BETWEEN 1 AND 8
    );

COMMENT ON COLUMN backup_tasks.max_parallel_chunks IS '单文件在途分块数（1-16）；性能参数，限速用 bandwidth_limit_kbps';
COMMENT ON COLUMN backup_tasks.max_parallel_files IS '同时在传的文件数（1-8）；不放大在途分块总数';
COMMENT ON COLUMN backup_tasks.max_parallel_hashes IS '预检并发哈希的文件数（1-8）';
