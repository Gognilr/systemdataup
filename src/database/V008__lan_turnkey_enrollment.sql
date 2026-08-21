-- LAN Turnkey 重新登记：已吊销或已禁用的旧实例允许同一 machine_id 建立新身份，
-- 活跃/待审批实例仍保持唯一，避免静默覆盖在线客户端。
ALTER TABLE clients DROP CONSTRAINT IF EXISTS uq_clients_machine_id;

CREATE UNIQUE INDEX IF NOT EXISTS uq_clients_machine_id_active
    ON clients (machine_id)
    WHERE status NOT IN ('revoked', 'disabled');
