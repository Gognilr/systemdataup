-- V007: Agent 本地托盘提示队列
-- 服务端只保存短期、去重后的提示；客户端通过心跳领取后在本机去重显示。
CREATE TABLE IF NOT EXISTS agent_notifications (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id       uuid NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
    kind            varchar(32) NOT NULL,
    severity        varchar(16) NOT NULL,
    title           varchar(255) NOT NULL,
    message         varchar(2000),
    dedupe_key      varchar(255) NOT NULL,
    alert_id        uuid REFERENCES alerts(id) ON DELETE SET NULL,
    backup_set_id   uuid REFERENCES backup_sets(id) ON DELETE SET NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_notifications_client_dedupe
    ON agent_notifications (client_id, dedupe_key);

CREATE INDEX IF NOT EXISTS idx_agent_notifications_client_created
    ON agent_notifications (client_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_agent_notifications_expires
    ON agent_notifications (expires_at);

COMMENT ON TABLE agent_notifications IS '发送给 Windows Agent 托盘的短期提示消息';
