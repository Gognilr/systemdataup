-- V041 客户端升级下发：分批推进 + 按上报版本判成功（整改清单 2026-09-10 · R20）
--
-- 此前升级没有任何服务端状态：下发一条 upgrade_agent 指令、Agent 回一句
-- 「UPGRADE_STAGED 成功」，界面就此结束。而那句成功说的是「包解压好了」，
-- 不是「装上了」——真相是运行的程序一个字节没变。
--
-- 这两张表要解开的是同一个问题的两半：
--   1. **成功的定义换人来下**：不再拿「指令执行完了」当成功，而是等这台机器
--      心跳回来、并且自报的版本号确实变成了目标版本。指令执行完只是过程，
--      版本号变了才是结果——把这两件事混为一谈正是这个缺陷的形状。
--   2. **分批（金丝雀）**：先 1 台，确认它真的活着回来了再放 5 台，再铺开。
--      一个坏包同时推给 29 台就是 29 次上门；而升级失败的机器连不上服务端，
--      连修复指令都收不到，只能人到现场。

CREATE TABLE IF NOT EXISTS agent_upgrades (
    id uuid PRIMARY KEY,
    target_version varchar(32) NOT NULL,
    package_url varchar(2048) NOT NULL,
    package_sha256 varchar(64) NOT NULL,
    note varchar(500) NULL,
    -- pending / running / succeeded / failed / cancelled
    status varchar(20) NOT NULL,
    -- 每批放几台，逗号分隔，最后一个 0 表示「剩下的全放」。默认 '1,5,0'。
    batch_plan varchar(64) NOT NULL,
    -- 当前正在观察的批次序号；-1 表示一批都还没发。
    current_batch int NOT NULL DEFAULT -1,
    batch_started_at timestamptz NULL,
    created_by uuid NULL,
    created_by_name varchar(100) NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz NULL,
    failure_reason text NULL
);

COMMENT ON TABLE agent_upgrades IS '一次分批升级下发（R20）；成功与否由客户端上报的版本号决定，不是由指令回报决定';
COMMENT ON COLUMN agent_upgrades.batch_plan IS '每批台数，逗号分隔，末位 0 表示剩余全部（默认 1,5,0 即金丝雀 1 台 → 5 台 → 铺开）';

CREATE TABLE IF NOT EXISTS agent_upgrade_targets (
    id uuid PRIMARY KEY,
    upgrade_id uuid NOT NULL REFERENCES agent_upgrades(id) ON DELETE CASCADE,
    client_id uuid NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
    batch_index int NOT NULL,
    -- waiting / dispatched / succeeded / failed / cancelled
    status varchar(20) NOT NULL,
    command_id uuid NULL,
    version_before varchar(64) NULL,
    reported_version varchar(64) NULL,
    dispatched_at timestamptz NULL,
    completed_at timestamptz NULL,
    error_code varchar(64) NULL,
    error_message text NULL,
    CONSTRAINT uq_agent_upgrade_targets_client UNIQUE (upgrade_id, client_id)
);

COMMENT ON COLUMN agent_upgrade_targets.version_before IS '下发那一刻库里记的版本号；用来分辨「版本号变了」与「本来就是这一版」';
COMMENT ON COLUMN agent_upgrade_targets.reported_version IS '升级之后这台机器心跳自报的版本号——判成功的唯一依据';

CREATE INDEX IF NOT EXISTS idx_agent_upgrade_targets_upgrade ON agent_upgrade_targets(upgrade_id, batch_index);
CREATE INDEX IF NOT EXISTS idx_agent_upgrade_targets_client ON agent_upgrade_targets(client_id, status);
CREATE INDEX IF NOT EXISTS idx_agent_upgrades_status ON agent_upgrades(status, created_at DESC);

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
VALUES
    -- 一台机器从下发到「心跳回来且版本号变了」的等待上限。
    -- 30 分钟：下载 + 校验 + 等一个空闲窗口 + 停服务换文件起服务，正常几分钟，
    -- 而 Agent 会把升级推迟到空闲点（正在传一份大备份时不打断），要留出这段余量。
    ('agent_upgrade_timeout_minutes', '30'::jsonb, false, now())
ON CONFLICT (setting_key) DO NOTHING;
