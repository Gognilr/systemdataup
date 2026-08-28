-- =====================================================================
-- V028：备份计划（多任务编组、按顺序执行）+ 通用顺序执行队列
--
-- 两件事共用一套机制：
--
--   A 备份计划：调度此前完全在客户端（Agent 按各任务的 cron 自己触发扫描），
--     因此「先备 A 再备 B」这种跨客户端的顺序执行客户端做不到——
--     A 机器无从知道 B 机器传完没有。服务端因此需要一个自己的调度器。
--   B 批量备份的并发闸：upload_batches.max_concurrent_clients 这个字段一直存在、
--     历史页还显示成「2 客户端 × 1」，但没有任何调度逻辑读它——
--     BatchOperationService 是一次性把所有候选的上传指令全下发出去。
--
-- 所以队列只做一套：execution_runs（一次执行）+ execution_run_items（其中的每一项），
-- 计划到点产生一次 run，批量上传也产生一次 run，由同一个 SequentialExecutionWorker
-- 按 max_concurrent 放行——前一项终结（成功/失败/超时）才放下一项。
--
-- max_concurrent_per_client 一并删除：Agent 天生串行执行指令（AgentWorker），
-- 这个值填几都一样。留着只会让人以为调了有用。
-- =====================================================================

-- --------------------------------------------------------------------
-- 域
-- --------------------------------------------------------------------
CREATE DOMAIN plan_schedule_kind AS varchar(32)
    CHECK (VALUE IN ('daily', 'weekly'));

CREATE DOMAIN execution_run_kind AS varchar(32)
    CHECK (VALUE IN ('backup_plan', 'upload_batch'));

CREATE DOMAIN execution_item_status AS varchar(32)
    CHECK (VALUE IN ('pending', 'running', 'succeeded', 'failed', 'timeout', 'skipped', 'cancelled'));

-- --------------------------------------------------------------------
-- 备份计划
-- --------------------------------------------------------------------
CREATE TABLE backup_plans (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name                 varchar(128) NOT NULL,
    enabled              boolean      NOT NULL DEFAULT true,
    schedule_kind        plan_schedule_kind NOT NULL DEFAULT 'daily',
    -- 执行时刻（计划自己的时区里的本地时刻）
    run_at               time         NOT NULL DEFAULT '02:00',
    -- weekly 时生效：ISO 周几，1=周一 … 7=周日，逗号分隔（如 '1,3,5'）
    days_of_week         varchar(32),
    timezone             varchar(64)  NOT NULL DEFAULT 'Asia/Shanghai',
    -- 1 = 严格按顺序一个一个来；N = 最多 N 个同时跑
    max_concurrent       int          NOT NULL DEFAULT 1,
    -- 单项超时（分钟）：一台关机的客户端不能把整晚的计划拖死
    item_timeout_minutes int          NOT NULL DEFAULT 240,
    last_run_at          timestamptz,
    created_by           UUID REFERENCES users(id),
    created_at           timestamptz  NOT NULL DEFAULT now(),
    updated_at           timestamptz  NOT NULL DEFAULT now(),

    CONSTRAINT ck_backup_plans_concurrency CHECK (max_concurrent BETWEEN 1 AND 50),
    CONSTRAINT ck_backup_plans_timeout     CHECK (item_timeout_minutes BETWEEN 5 AND 10080)
);

COMMENT ON TABLE backup_plans IS '备份计划：把多个备份任务编成一组，到点按顺序驱动';

CREATE UNIQUE INDEX uq_backup_plans_name ON backup_plans (name);

CREATE TABLE backup_plan_items (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_id    UUID NOT NULL REFERENCES backup_plans(id) ON DELETE CASCADE,
    task_id    UUID NOT NULL REFERENCES backup_tasks(id) ON DELETE CASCADE,
    sort_order int  NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- 一个任务只能属于一个计划：否则「由计划 X 驱动」这句话没法说，
-- 两个计划同时驱动同一个任务也会让顺序执行失去意义。
CREATE UNIQUE INDEX uq_backup_plan_items_task ON backup_plan_items (task_id);
CREATE INDEX idx_backup_plan_items_plan ON backup_plan_items (plan_id, sort_order);

COMMENT ON TABLE backup_plan_items IS '备份计划的编组成员与执行顺序';

-- --------------------------------------------------------------------
-- 顺序执行队列（备份计划与批量上传共用）
-- --------------------------------------------------------------------
CREATE TABLE execution_runs (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    kind                 execution_run_kind NOT NULL,
    plan_id              UUID REFERENCES backup_plans(id) ON DELETE SET NULL,
    upload_batch_id      UUID REFERENCES upload_batches(id) ON DELETE CASCADE,
    name                 varchar(255),
    status               batch_status NOT NULL DEFAULT 'pending',
    max_concurrent       int          NOT NULL DEFAULT 1,
    item_timeout_minutes int          NOT NULL DEFAULT 240,
    -- 计划到点触发写 'schedule'，人点「立即执行」写 'manual'
    trigger_source       varchar(32)  NOT NULL DEFAULT 'schedule',
    total_items          int          NOT NULL DEFAULT 0,
    succeeded_items      int          NOT NULL DEFAULT 0,
    failed_items         int          NOT NULL DEFAULT 0,
    triggered_by         UUID REFERENCES users(id),
    -- 计划的这一次「应执行时刻」，同一时刻只允许产生一次 run（补跑与重复触发的闸）
    scheduled_for        timestamptz,
    created_at           timestamptz  NOT NULL DEFAULT now(),
    started_at           timestamptz,
    finished_at          timestamptz,

    CONSTRAINT ck_execution_runs_concurrency CHECK (max_concurrent BETWEEN 1 AND 50)
);

COMMENT ON TABLE execution_runs IS '一次顺序执行（备份计划到点执行 / 一次批量上传）';

CREATE INDEX idx_execution_runs_status ON execution_runs (status);
CREATE UNIQUE INDEX uq_execution_runs_plan_scheduled
    ON execution_runs (plan_id, scheduled_for)
    WHERE plan_id IS NOT NULL AND scheduled_for IS NOT NULL;

CREATE TABLE execution_run_items (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id                  UUID NOT NULL REFERENCES execution_runs(id) ON DELETE CASCADE,
    sort_order              int  NOT NULL DEFAULT 0,
    client_id               UUID NOT NULL REFERENCES clients(id),
    task_id                 UUID NOT NULL REFERENCES backup_tasks(id) ON DELETE CASCADE,
    candidate_backup_set_id UUID REFERENCES candidate_backup_sets(id) ON DELETE SET NULL,
    -- precheck_task（计划驱动一个任务）或 upload_candidate（批量上传一个候选）
    command_type            command_type NOT NULL,
    status                  execution_item_status NOT NULL DEFAULT 'pending',
    command_id              UUID REFERENCES commands(id) ON DELETE SET NULL,
    upload_session_id       UUID REFERENCES upload_sessions(id) ON DELETE SET NULL,
    started_at              timestamptz,
    finished_at             timestamptz,
    message                 varchar(1000)
);

COMMENT ON TABLE execution_run_items IS '顺序执行队列里的一项：一个任务或一个候选备份集';

CREATE INDEX idx_execution_run_items_run ON execution_run_items (run_id, sort_order);
CREATE INDEX idx_execution_run_items_status ON execution_run_items (status);

-- --------------------------------------------------------------------
-- 批量上传：并发闸接线，删掉那个永远不起作用的字段
-- --------------------------------------------------------------------
ALTER TABLE upload_batches DROP COLUMN IF EXISTS max_concurrent_per_client;

-- --------------------------------------------------------------------
-- 配置
-- --------------------------------------------------------------------
INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by)
VALUES
    -- 队列推进间隔：太长会让「前一个传完到下一个开跑」之间空等
    ('execution_queue_poll_seconds', '15'::jsonb, false, NULL),
    -- 预检通过后等多久还没有上传会话，就判定为「这次没有新备份要传」
    ('execution_queue_no_upload_grace_minutes', '10'::jsonb, false, NULL),
    -- 计划补跑上限：服务端停机超过这个小时数，过期的那次计划不再补跑
    ('execution_queue_catchup_hours', '12'::jsonb, false, NULL)
ON CONFLICT (setting_key) DO NOTHING;
