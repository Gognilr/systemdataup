-- =====================================================================
-- V023：通知投递补「已取消」状态与标题前缀（审计批次 G：G-11 / G-12）
--
-- G-12：RecoverAsync 原先只把告警置为 recovered，对应的 notification_deliveries
-- 仍是 pending；派发器只看投递记录自身的状态，不回查告警是否已恢复。
-- 于是一次短暂抖动产生的告警邮件，会在告警早已恢复之后继续按 5 分钟间隔
-- 重试三轮才罢休。补一格 cancelled 让「不必再发」有地方表达。
--
-- G-11：等级提升（Warning → Critical）补发的那一封通知需要「【已升级】」前缀，
-- 否则收件人看到的是一封和几天前一模一样的邮件，正好会被当成重发忽略掉。
-- 前缀放在投递记录而不是改 alerts.title：告警描述的那件事本身没变，
-- 而且同一条告警可能升级多次，改标题会让前缀越叠越长。
--
-- C# 枚举 NotificationStatus 与本域必须同步，否则 EnumDomainParityTests 立刻红。
-- =====================================================================

-- ============================================================================
-- 1. notification_status：新增 cancelled
-- ============================================================================

DO $$
DECLARE
    constraint_name text;
BEGIN
    SELECT con.conname INTO constraint_name
      FROM pg_constraint con
      JOIN pg_type typ ON typ.oid = con.contypid
     WHERE typ.typname = 'notification_status'
       AND con.contype = 'c'
     LIMIT 1;

    IF constraint_name IS NOT NULL THEN
        EXECUTE format('ALTER DOMAIN notification_status DROP CONSTRAINT %I', constraint_name);
    END IF;
END
$$;

ALTER DOMAIN notification_status ADD CONSTRAINT notification_status_check
    CHECK (VALUE IN ('pending', 'sent', 'failed', 'cancelled'));

-- ============================================================================
-- 2. notification_deliveries.title_prefix
-- ============================================================================

ALTER TABLE notification_deliveries
    ADD COLUMN title_prefix varchar(32);

COMMENT ON COLUMN notification_deliveries.title_prefix IS '通知标题前缀，等级提升补发的那一封为「【已升级】」；为空表示按告警标题原样发送';
