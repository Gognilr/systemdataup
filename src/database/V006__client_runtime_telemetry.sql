-- V006: 客户端运行状态扩展
-- 真实 CPU/内存采集、资源历史查询和当前 Windows 用户会话展示。

ALTER TABLE client_heartbeats
    ADD COLUMN IF NOT EXISTS agent_cpu_percent numeric(5,2),
    ADD COLUMN IF NOT EXISTS memory_total_bytes bigint;

CREATE TABLE IF NOT EXISTS client_user_sessions (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id       uuid NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
    session_id      int NOT NULL,
    username        varchar(255),
    domain_name     varchar(255),
    state           varchar(32) NOT NULL,
    client_name     varchar(255),
    client_address  varchar(128),
    is_remote       boolean NOT NULL DEFAULT false,
    logon_at        timestamptz,
    sampled_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_client_user_sessions_client_session UNIQUE (client_id, session_id)
);

CREATE INDEX IF NOT EXISTS idx_client_user_sessions_client_sampled
    ON client_user_sessions (client_id, sampled_at DESC);

COMMENT ON TABLE client_user_sessions IS '客户端当前 Windows 用户会话；每次心跳全量刷新';
COMMENT ON COLUMN client_heartbeats.agent_cpu_percent IS 'Agent 进程 CPU 使用率';
COMMENT ON COLUMN client_heartbeats.memory_total_bytes IS '客户端物理内存总量';

INSERT INTO system_settings (setting_key, setting_value, encrypted)
VALUES
    ('client_cpu_alert_percent', '85', false),
    ('client_memory_alert_percent', '90', false),
    ('client_disk_free_alert_percent', '10', false)
ON CONFLICT (setting_key) DO NOTHING;
