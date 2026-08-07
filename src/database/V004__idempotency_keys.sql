-- =============================================================
-- V004: 通用幂等键表（设计书 8.5 幂等要求）
-- 背景：恢复请求创建（19.1）原先用进程内存字典做 Idempotency-Key
--       去重，重启即失效、多实例不共享。落库后幂等记录持久化，
--       重启与多实例部署均保持一致。
-- 设计：
--   scope            业务作用域（如 restore.create），供后续其他
--                    幂等端点复用，避免每个业务加一列
--   idempotency_key  客户端提供的 Idempotency-Key 头
--   resource_id      首次请求创建的资源 Id（重放时原样返回）
--   expires_at       幂等有效期（恢复创建为 10 分钟），过期行由
--                    服务端在查询时顺手清理
--   主键 (scope, idempotency_key) 提供并发兜底：两个相同键的请求
--   并发到达时，后提交者触发唯一冲突并整体回滚，改回首查结果。
-- =============================================================

CREATE TABLE idempotency_keys (
    scope           varchar(64)  NOT NULL,
    idempotency_key varchar(128) NOT NULL,
    resource_id     uuid         NOT NULL,
    created_at      timestamptz  NOT NULL DEFAULT now(),
    expires_at      timestamptz  NOT NULL,
    PRIMARY KEY (scope, idempotency_key)
);

CREATE INDEX ix_idempotency_keys_expires_at ON idempotency_keys (expires_at);

COMMENT ON TABLE idempotency_keys IS '通用幂等键（Idempotency-Key 持久化，重放返回首次结果）';
COMMENT ON COLUMN idempotency_keys.scope IS '业务作用域（如 restore.create）';
COMMENT ON COLUMN idempotency_keys.resource_id IS '首次请求创建的资源 Id（重放时原样返回）';
COMMENT ON COLUMN idempotency_keys.expires_at IS '幂等有效期，过期后同键视为新请求';
