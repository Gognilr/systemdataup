-- V010：心跳快照、按状态变化留痕以及托盘消息投递状态。

ALTER DOMAIN certificate_status
    DROP CONSTRAINT IF EXISTS certificate_status_check;
ALTER DOMAIN certificate_status
    ADD CONSTRAINT certificate_status_check
    CHECK (VALUE IN ('active', 'revoked', 'expired', 'superseded'));

ALTER TABLE monitored_service_definitions
    ADD COLUMN IF NOT EXISTS current_actual_state service_actual_state,
    ADD COLUMN IF NOT EXISTS current_start_type service_start_type,
    ADD COLUMN IF NOT EXISTS current_sampled_at timestamptz;

-- 为已经存在的服务定义回填最近一次状态，避免升级后管理页面暂时失去当前状态。
UPDATE monitored_service_definitions AS definition
SET current_actual_state = latest.actual_state,
    current_start_type = latest.start_type,
    current_sampled_at = latest.sampled_at
FROM (
    SELECT DISTINCT ON (definition_id)
           definition_id,
           actual_state,
           start_type,
           sampled_at
    FROM client_service_states
    ORDER BY definition_id, sampled_at DESC, id DESC
) AS latest
WHERE definition.id = latest.definition_id;

ALTER TABLE agent_notifications
    ADD COLUMN IF NOT EXISTS delivered_at timestamptz;

CREATE INDEX IF NOT EXISTS idx_agent_notifications_client_delivery
    ON agent_notifications (client_id, delivered_at, created_at);
