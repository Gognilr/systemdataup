-- V009：遥测分区兜底、分区维护和保留期默认值。
-- DEFAULT 分区保证维护任务短暂落后时写入不会静默失败；月度分区由
-- PartitionMaintenanceWorker 提前创建并在保留期到达后 DROP。
ALTER TABLE clients
    ADD COLUMN IF NOT EXISTS enrollment_mode varchar(16) NOT NULL DEFAULT 'secure';

CREATE TABLE IF NOT EXISTS client_heartbeats_default
    PARTITION OF client_heartbeats DEFAULT;

CREATE TABLE IF NOT EXISTS audit_logs_default
    PARTITION OF audit_logs DEFAULT;

INSERT INTO system_settings (setting_key, setting_value, encrypted)
VALUES
    ('telemetry_service_state_retention_days', '7'::jsonb, false),
    ('telemetry_heartbeat_retention_days', '30'::jsonb, false),
    ('partition_maintenance_interval_hours', '24'::jsonb, false)
ON CONFLICT (setting_key) DO NOTHING;
