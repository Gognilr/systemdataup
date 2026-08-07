-- =============================================================
-- V002: 刷新令牌表 + 批量上传幂等键
-- 依据《详细数据库及接口设计书》：
--   9.3  退出时服务端吊销刷新令牌
--   23.2 Refresh Token 可撤销
--   8.5  创建批量上传必须支持 Idempotency-Key
-- 说明：V001 未包含刷新令牌存储，无法满足"可吊销"要求，
--       故本迁移补充 refresh_tokens 表（增量迁移，不改动 V001）。
-- =============================================================

-- 4.7 管理员刷新令牌（只存哈希，明文仅一次性返回客户端）
CREATE TABLE refresh_tokens (
    id                uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id           uuid         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash        varchar(128) NOT NULL,
    created_at        timestamptz  NOT NULL DEFAULT now(),
    expires_at        timestamptz  NOT NULL,
    revoked_at        timestamptz,
    revoke_reason     varchar(64),
    replaced_by_hash  varchar(128),
    created_ip        varchar(64),
    user_agent        varchar(512),
    CONSTRAINT uq_refresh_tokens_hash UNIQUE (token_hash)
);

CREATE INDEX ix_refresh_tokens_user_id ON refresh_tokens (user_id);
CREATE INDEX ix_refresh_tokens_expires_at ON refresh_tokens (expires_at);

COMMENT ON TABLE refresh_tokens IS '管理员刷新令牌（哈希存储，可吊销，支持轮换）';

-- upload_batches 增加幂等键（8.5 幂等要求）
ALTER TABLE upload_batches ADD COLUMN idempotency_key varchar(128);
ALTER TABLE upload_batches
    ADD CONSTRAINT uq_upload_batches_idempotency UNIQUE (idempotency_key);

-- 注册-审批-签发证书支撑列（10.1 提交注册携带公钥，审批时签发证书）：
-- clients.public_key           注册时提交的客户端公钥（Base64 SPKI），审批签发证书使用
-- client_certificates.certificate_pem  签发出的客户端证书（PEM），供注册结果轮询返回
ALTER TABLE clients ADD COLUMN public_key text;
COMMENT ON COLUMN clients.public_key IS '注册时提交的客户端公钥（Base64 SPKI），审批签发证书使用';

ALTER TABLE client_certificates ADD COLUMN certificate_pem text;
COMMENT ON COLUMN client_certificates.certificate_pem IS '签发出的客户端证书（PEM），注册结果查询返回';

-- 任务暂停/恢复支撑列（16.5/16.6 暂停后恢复需还原暂停前模式）
ALTER TABLE backup_tasks ADD COLUMN previous_task_mode task_mode;
COMMENT ON COLUMN backup_tasks.previous_task_mode IS '暂停前的任务模式（恢复时还原）';
