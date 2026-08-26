-- V020：预检幽灵状态清理 + 告警指派/临时静默
--
-- 一、precheck_status 删除三个从未有生产者的中间态（Scanning / CandidateFound / WaitingStable）：
-- 预检是一次性上报，不存在中间态。C# 枚举与数据库 DOMAIN 的 CHECK 约束必须同步改，
-- 否则 EnumDomainParityTests 立刻红。
--
-- 二、backup_set_status 删除 RetentionPending：recycle_bin 本身已是反悔窗口，
-- 再加一层"下一轮将被回收"的预告状态没有增量价值。
--
-- 三、告警中心补齐"指派"与"临时静默"（功能说明书 8.18）：
-- alerts 加 assigned_to / assigned_at；新增 alert_silences 表，
-- AlertingService.RaiseAsync 建新告警前查静默命中，命中则只累加次数、不发通知、不推送 Agent 托盘。

-- ============================================================================
-- 1. precheck_status：删除 scanning / candidate_found / waiting_stable
-- ============================================================================

DO $$
DECLARE
    constraint_name text;
BEGIN
    SELECT con.conname INTO constraint_name
      FROM pg_constraint con
      JOIN pg_type typ ON typ.oid = con.contypid
     WHERE typ.typname = 'precheck_status'
       AND con.contype = 'c'
     LIMIT 1;

    IF constraint_name IS NOT NULL THEN
        EXECUTE format('ALTER DOMAIN precheck_status DROP CONSTRAINT %I', constraint_name);
    END IF;
END
$$;

ALTER DOMAIN precheck_status ADD CONSTRAINT precheck_status_check
    CHECK (VALUE IN (
        'not_scanned',
        'passed', 'no_new_backup', 'still_changing',
        'required_file_missing', 'size_abnormal',
        'path_not_found', 'access_denied', 'failed'
    ));

-- ============================================================================
-- 2. backup_set_status：删除 retention_pending
-- ============================================================================

DO $$
DECLARE
    constraint_name text;
BEGIN
    SELECT con.conname INTO constraint_name
      FROM pg_constraint con
      JOIN pg_type typ ON typ.oid = con.contypid
     WHERE typ.typname = 'backup_set_status'
       AND con.contype = 'c'
     LIMIT 1;

    IF constraint_name IS NOT NULL THEN
        EXECUTE format('ALTER DOMAIN backup_set_status DROP CONSTRAINT %I', constraint_name);
    END IF;
END
$$;

ALTER DOMAIN backup_set_status ADD CONSTRAINT backup_set_status_check
    CHECK (VALUE IN (
        'verifying', 'available', 'verification_failed',
        'quarantined', 'recycle_bin', 'deleted'
    ));

-- ============================================================================
-- 3. 告警指派
-- ============================================================================

ALTER TABLE alerts
    ADD COLUMN assigned_to uuid REFERENCES users(id),
    ADD COLUMN assigned_at timestamptz;

CREATE INDEX idx_alerts_assigned_to ON alerts (assigned_to);

-- ============================================================================
-- 4. 告警临时静默
-- ============================================================================

CREATE TABLE alert_silences (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    alert_key_pattern   varchar(255) NOT NULL,
    reason              varchar(1000) NOT NULL,
    until               timestamptz NOT NULL,
    created_by          UUID NOT NULL REFERENCES users(id),
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_alert_silences_until ON alert_silences (until);

COMMENT ON TABLE alert_silences IS '告警临时静默：alert_key_pattern 用 SQL LIKE 语法（% 通配），命中期间不发通知、不推送 Agent 托盘，但告警计数仍照常累加';
