-- ============================================================================
-- 轻量级集中备份采集与监控系统 - 数据库初始化脚本
-- 版本: V1.0
-- 数据库: PostgreSQL 15+
-- 字符集: UTF-8
-- 时间标准: 数据库存储 UTC
-- ============================================================================

BEGIN;

-- ============================================================================
-- 1. 扩展
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";  -- gen_random_uuid()

-- ============================================================================
-- 2. 枚举类型（使用 DOMAIN + CHECK 约束，便于后续扩展）
-- ============================================================================

-- 2.1 客户端状态
CREATE DOMAIN client_status AS varchar(32)
    CHECK (VALUE IN (
        'pending_approval', 'online', 'suspected_offline',
        'offline', 'disabled', 'revoked', 'certificate_expired'
    ));

-- 2.2 任务模式
CREATE DOMAIN task_mode AS varchar(32)
    CHECK (VALUE IN (
        'automatic', 'approval_required', 'manual',
        'monitor_only', 'paused'
    ));

-- 2.3 识别类型
CREATE DOMAIN recognizer_type AS varchar(32)
    CHECK (VALUE IN (
        'latest_single_file', 'latest_directory',
        'multi_file_set', 'subdirectory_units'
    ));

-- 2.4 预检状态
CREATE DOMAIN precheck_status AS varchar(32)
    CHECK (VALUE IN (
        'not_scanned', 'scanning', 'candidate_found', 'waiting_stable',
        'passed', 'no_new_backup', 'still_changing',
        'required_file_missing', 'size_abnormal',
        'path_not_found', 'access_denied', 'failed'
    ));

-- 2.5 指令状态
CREATE DOMAIN command_status AS varchar(32)
    CHECK (VALUE IN (
        'pending', 'claimed', 'running', 'succeeded',
        'failed', 'cancelled', 'expired', 'rejected'
    ));

-- 2.6 指令类型
CREATE DOMAIN command_type AS varchar(32)
    CHECK (VALUE IN (
        'precheck_task', 'precheck_all',
        'upload_candidate', 'upload_latest',
        'pause_upload', 'resume_upload', 'cancel_upload',
        'rescan', 'rehash', 'sync_config',
        'refresh_metrics', 'upgrade_agent'
    ));

-- 2.7 上传会话状态
CREATE DOMAIN upload_status AS varchar(32)
    CHECK (VALUE IN (
        'created', 'waiting_permission', 'uploading', 'paused',
        'retry_wait', 'received', 'verifying', 'verified',
        'committed', 'failed', 'cancelled', 'expired'
    ));

-- 2.8 备份版本状态
CREATE DOMAIN backup_set_status AS varchar(32)
    CHECK (VALUE IN (
        'verifying', 'available', 'verification_failed',
        'quarantined', 'retention_pending', 'recycle_bin', 'deleted'
    ));

-- 2.9 告警等级
CREATE DOMAIN alert_level AS varchar(32)
    CHECK (VALUE IN ('critical', 'warning', 'notice'));

-- 2.10 告警状态
CREATE DOMAIN alert_status AS varchar(32)
    CHECK (VALUE IN (
        'open', 'acknowledged', 'in_progress',
        'recovered', 'closed', 'ignored'
    ));

-- 辅助状态枚举

-- 用户状态
CREATE DOMAIN user_status AS varchar(32)
    CHECK (VALUE IN ('active', 'locked', 'disabled'));

-- 注册令牌状态
CREATE DOMAIN registration_token_status AS varchar(32)
    CHECK (VALUE IN ('active', 'revoked', 'expired'));

-- 证书状态
CREATE DOMAIN certificate_status AS varchar(32)
    CHECK (VALUE IN ('active', 'revoked', 'expired'));

-- 上传文件状态
CREATE DOMAIN upload_file_status AS varchar(32)
    CHECK (VALUE IN ('pending', 'uploading', 'received', 'verified', 'failed'));

-- 上传分块状态
CREATE DOMAIN upload_chunk_status AS varchar(32)
    CHECK (VALUE IN ('pending', 'received', 'verified', 'failed'));

-- 恢复请求状态
CREATE DOMAIN restore_request_status AS varchar(32)
    CHECK (VALUE IN (
        'requested', 'verifying', 'ready',
        'downloading', 'completed', 'failed', 'expired'
    ));

-- 通知发送状态
CREATE DOMAIN notification_status AS varchar(32)
    CHECK (VALUE IN ('pending', 'sent', 'failed'));

-- 审计结果
CREATE DOMAIN audit_result AS varchar(32)
    CHECK (VALUE IN ('success', 'failure'));

-- 服务期望/实际状态
CREATE DOMAIN service_expected_state AS varchar(32)
    CHECK (VALUE IN ('running', 'stopped'));

CREATE DOMAIN service_actual_state AS varchar(32)
    CHECK (VALUE IN ('running', 'stopped', 'paused', 'not_found'));

CREATE DOMAIN service_start_type AS varchar(32)
    CHECK (VALUE IN ('auto', 'manual', 'disabled'));

-- 文件校验状态
CREATE DOMAIN verification_status AS varchar(32)
    CHECK (VALUE IN ('verified', 'failed'));

-- 批次状态
CREATE DOMAIN batch_status AS varchar(32)
    CHECK (VALUE IN (
        'pending', 'running', 'completed',
        'partial', 'failed', 'cancelled'
    ));

-- 角色范围类型
CREATE DOMAIN scope_type AS varchar(32)
    CHECK (VALUE IN ('global', 'client_group'));

-- 通知渠道
CREATE DOMAIN notification_channel AS varchar(32)
    CHECK (VALUE IN ('email', 'wecom', 'dingtalk'));

-- 重要等级
CREATE DOMAIN importance_level AS varchar(32)
    CHECK (VALUE IN ('low', 'normal', 'high', 'critical'));


-- ============================================================================
-- 3. 核心表
-- ============================================================================

-- --------------------------------------------------------------------------
-- 3.1 用户表
-- --------------------------------------------------------------------------
CREATE TABLE users (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username            varchar(64)     NOT NULL,
    display_name        varchar(128)    NOT NULL,
    password_hash       varchar(255)    NOT NULL,
    email               varchar(255),
    mobile              varchar(32),
    status              user_status     NOT NULL DEFAULT 'active',
    failed_login_count  int             NOT NULL DEFAULT 0,
    locked_until        timestamptz,
    last_login_at       timestamptz,
    password_changed_at timestamptz     NOT NULL DEFAULT now(),
    mfa_enabled         boolean         NOT NULL DEFAULT false,
    created_at          timestamptz     NOT NULL DEFAULT now(),
    updated_at          timestamptz     NOT NULL DEFAULT now(),
    row_version         bigint          NOT NULL DEFAULT 1,

    CONSTRAINT uq_users_username UNIQUE (username)
);

CREATE INDEX idx_users_status ON users (status);

COMMENT ON TABLE users IS '系统管理用户（管理员、操作员等）';
COMMENT ON COLUMN users.password_hash IS '必须使用 Argon2id/bcrypt/PBKDF2，禁止可逆加密';

-- --------------------------------------------------------------------------
-- 3.2 角色表
-- --------------------------------------------------------------------------
CREATE TABLE roles (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code        varchar(64)     NOT NULL,
    name        varchar(128)    NOT NULL,
    description varchar(500),
    is_system   boolean         NOT NULL DEFAULT false,
    created_at  timestamptz     NOT NULL DEFAULT now(),
    updated_at  timestamptz     NOT NULL DEFAULT now(),

    CONSTRAINT uq_roles_code UNIQUE (code)
);

COMMENT ON TABLE roles IS 'RBAC 角色，内置 4 个系统角色';

-- --------------------------------------------------------------------------
-- 3.3 权限表
-- --------------------------------------------------------------------------
CREATE TABLE permissions (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code        varchar(128)    NOT NULL,
    name        varchar(128)    NOT NULL,
    module      varchar(64),
    description varchar(500),

    CONSTRAINT uq_permissions_code UNIQUE (code)
);

COMMENT ON TABLE permissions IS '系统权限定义';

-- --------------------------------------------------------------------------
-- 3.4 用户角色关联表
-- --------------------------------------------------------------------------
CREATE TABLE user_roles (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id     UUID        NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    scope_type  scope_type  NOT NULL DEFAULT 'global',
    scope_id    UUID,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- 表达式唯一约束不能用表级 UNIQUE 表达，改用函数式唯一索引
-- （scope_id 为 NULL 时按全零 UUID 参与唯一性判断）
CREATE UNIQUE INDEX uq_user_roles ON user_roles (user_id, role_id, scope_type, COALESCE(scope_id, '00000000-0000-0000-0000-000000000000'::uuid));

CREATE INDEX idx_user_roles_user_id ON user_roles (user_id);
CREATE INDEX idx_user_roles_role_id ON user_roles (role_id);

COMMENT ON TABLE user_roles IS '用户-角色分配，支持按分组限定范围';

-- --------------------------------------------------------------------------
-- 3.5 角色权限关联表
-- --------------------------------------------------------------------------
CREATE TABLE role_permissions (
    role_id         UUID        NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission_id   UUID        NOT NULL REFERENCES permissions(id) ON DELETE CASCADE,
    created_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT pk_role_permissions PRIMARY KEY (role_id, permission_id)
);

CREATE INDEX idx_role_permissions_role_id ON role_permissions (role_id);
CREATE INDEX idx_role_permissions_permission_id ON role_permissions (permission_id);

-- --------------------------------------------------------------------------
-- 3.6 客户端分组表
-- --------------------------------------------------------------------------
CREATE TABLE client_groups (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_id                   UUID REFERENCES client_groups(id),
    name                        varchar(128)    NOT NULL,
    code                        varchar(64)     NOT NULL,
    description                 varchar(500),
    default_bandwidth_limit_kbps int,
    default_concurrency         int             NOT NULL DEFAULT 1,
    created_at                  timestamptz     NOT NULL DEFAULT now(),
    updated_at                  timestamptz     NOT NULL DEFAULT now(),

    CONSTRAINT uq_client_groups_code UNIQUE (code)
);

CREATE INDEX idx_client_groups_parent_id ON client_groups (parent_id);

COMMENT ON TABLE client_groups IS '客户端分组（树形结构）';

-- --------------------------------------------------------------------------
-- 3.7 注册令牌表
-- --------------------------------------------------------------------------
CREATE TABLE registration_tokens (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_hash      varchar(255)    NOT NULL,
    name            varchar(128)    NOT NULL,
    client_group_id UUID REFERENCES client_groups(id),
    expires_at      timestamptz,
    max_uses        int             NOT NULL DEFAULT 1,
    used_count      int             NOT NULL DEFAULT 0,
    status          registration_token_status NOT NULL DEFAULT 'active',
    created_by      UUID REFERENCES users(id),
    created_at      timestamptz     NOT NULL DEFAULT now(),

    CONSTRAINT ck_registration_tokens_max_uses CHECK (max_uses > 0),
    CONSTRAINT ck_registration_tokens_used_count CHECK (used_count >= 0)
);

CREATE INDEX idx_registration_tokens_token_hash ON registration_tokens (token_hash);
CREATE INDEX idx_registration_tokens_status ON registration_tokens (status);

COMMENT ON TABLE registration_tokens IS '客户端注册令牌（只存哈希，明文不得入库）';

-- --------------------------------------------------------------------------
-- 3.8 客户端表
-- --------------------------------------------------------------------------
CREATE TABLE clients (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    machine_id              varchar(128)    NOT NULL,
    hostname                varchar(255)    NOT NULL,
    display_name            varchar(255)    NOT NULL,
    client_group_id         UUID REFERENCES client_groups(id),
    os_name                 varchar(255),
    os_version              varchar(128),
    architecture            varchar(32),
    agent_version           varchar(64),
    ip_addresses            jsonb,
    status                  client_status   NOT NULL DEFAULT 'pending_approval',
    approved_at             timestamptz,
    approved_by             UUID REFERENCES users(id),
    last_heartbeat_at       timestamptz,
    last_config_version     bigint          NOT NULL DEFAULT 0,
    certificate_thumbprint  varchar(128),
    certificate_expires_at  timestamptz,
    time_offset_seconds     int,
    notes                   varchar(1000),
    created_at              timestamptz     NOT NULL DEFAULT now(),
    updated_at              timestamptz     NOT NULL DEFAULT now(),
    row_version             bigint          NOT NULL DEFAULT 1,

    CONSTRAINT uq_clients_machine_id UNIQUE (machine_id)
);

CREATE INDEX idx_clients_status_heartbeat ON clients (status, last_heartbeat_at);
CREATE INDEX idx_clients_client_group_id ON clients (client_group_id);
CREATE INDEX idx_clients_hostname ON clients (hostname);

COMMENT ON TABLE clients IS '受管客户端（Agent 安装实例）';

-- --------------------------------------------------------------------------
-- 3.9 客户端证书表
-- --------------------------------------------------------------------------
CREATE TABLE client_certificates (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id       UUID            NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
    thumbprint      varchar(128)    NOT NULL,
    serial_number   varchar(128),
    issued_at       timestamptz     NOT NULL,
    expires_at      timestamptz     NOT NULL,
    status          certificate_status NOT NULL DEFAULT 'active',
    revoked_at      timestamptz,
    revoke_reason   varchar(500),

    CONSTRAINT uq_client_certificates_thumbprint UNIQUE (thumbprint)
);

CREATE INDEX idx_client_certificates_client_id ON client_certificates (client_id);
CREATE INDEX idx_client_certificates_status ON client_certificates (status);

-- --------------------------------------------------------------------------
-- 3.10 心跳表（按月分区）
-- --------------------------------------------------------------------------
CREATE TABLE client_heartbeats (
    id                      UUID NOT NULL DEFAULT gen_random_uuid(),
    client_id               UUID NOT NULL REFERENCES clients(id),
    received_at             timestamptz NOT NULL DEFAULT now(),
    client_time             timestamptz,
    agent_uptime_seconds    bigint,
    system_uptime_seconds   bigint,
    cpu_percent             decimal(5,2),
    memory_percent          decimal(5,2),
    memory_available_bytes  bigint,
    agent_memory_bytes      bigint,
    network_send_bps        bigint,
    network_receive_bps     bigint,
    active_command_count    int,
    active_upload_count     int,
    payload                 jsonb,

    CONSTRAINT pk_client_heartbeats PRIMARY KEY (id, received_at)
) PARTITION BY RANGE (received_at);

CREATE INDEX idx_client_heartbeats_client_received
    ON client_heartbeats (client_id, received_at DESC);
CREATE INDEX idx_client_heartbeats_received
    ON client_heartbeats (received_at);

COMMENT ON TABLE client_heartbeats IS '客户端心跳记录（按月分区，原始数据保留30天）';

-- 创建初始分区（当前月 + 下月）
CREATE TABLE client_heartbeats_y2026m07
    PARTITION OF client_heartbeats
    FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
CREATE TABLE client_heartbeats_y2026m08
    PARTITION OF client_heartbeats
    FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');
CREATE TABLE client_heartbeats_y2026m09
    PARTITION OF client_heartbeats
    FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');
CREATE TABLE client_heartbeats_y2026m10
    PARTITION OF client_heartbeats
    FOR VALUES FROM ('2026-10-01') TO ('2026-11-01');
CREATE TABLE client_heartbeats_y2026m11
    PARTITION OF client_heartbeats
    FOR VALUES FROM ('2026-11-01') TO ('2026-12-01');
CREATE TABLE client_heartbeats_y2026m12
    PARTITION OF client_heartbeats
    FOR VALUES FROM ('2026-12-01') TO ('2027-01-01');

-- --------------------------------------------------------------------------
-- 3.11 客户端磁盘状态表
-- --------------------------------------------------------------------------
CREATE TABLE client_disks (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id           UUID        NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
    drive_name          varchar(32) NOT NULL,
    volume_label        varchar(128),
    filesystem          varchar(32),
    total_bytes         bigint,
    free_bytes          bigint,
    is_source_volume    boolean     NOT NULL DEFAULT false,
    sampled_at          timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_client_disks_client_id ON client_disks (client_id);

COMMENT ON TABLE client_disks IS '客户端磁盘状态（每次心跳更新）';

-- --------------------------------------------------------------------------
-- 3.12 关键服务定义表
-- --------------------------------------------------------------------------
CREATE TABLE monitored_service_definitions (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id           UUID        NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
    service_name        varchar(255) NOT NULL,
    display_name        varchar(255) NOT NULL,
    expected_state      service_expected_state NOT NULL DEFAULT 'running',
    alert_on_mismatch   boolean     NOT NULL DEFAULT true,
    enabled             boolean     NOT NULL DEFAULT true
);

CREATE INDEX idx_monitored_service_definitions_client ON monitored_service_definitions (client_id);

COMMENT ON TABLE monitored_service_definitions IS '需要监控的 Windows 服务定义';

-- --------------------------------------------------------------------------
-- 3.13 关键服务状态表
-- --------------------------------------------------------------------------
CREATE TABLE client_service_states (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    definition_id   UUID        NOT NULL REFERENCES monitored_service_definitions(id) ON DELETE CASCADE,
    client_id       UUID        NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
    actual_state    service_actual_state NOT NULL,
    start_type      service_start_type NOT NULL DEFAULT 'auto',
    sampled_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_client_service_states_client ON client_service_states (client_id);
CREATE INDEX idx_client_service_states_definition ON client_service_states (definition_id);

-- --------------------------------------------------------------------------
-- 3.14 任务模板表
-- --------------------------------------------------------------------------
CREATE TABLE backup_task_templates (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            varchar(64)     NOT NULL,
    name            varchar(128)    NOT NULL,
    recognizer_type recognizer_type NOT NULL,
    default_config  jsonb,
    is_system       boolean         NOT NULL DEFAULT false,
    version         int             NOT NULL DEFAULT 1,
    created_at      timestamptz     NOT NULL DEFAULT now(),
    updated_at      timestamptz     NOT NULL DEFAULT now(),

    CONSTRAINT uq_backup_task_templates_code UNIQUE (code)
);

COMMENT ON TABLE backup_task_templates IS '备份任务模板（可快速创建任务）';

-- --------------------------------------------------------------------------
-- 3.15 备份任务表
-- --------------------------------------------------------------------------
CREATE TABLE backup_tasks (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id                   UUID        NOT NULL REFERENCES clients(id),
    template_id                 UUID REFERENCES backup_task_templates(id),
    name                        varchar(255) NOT NULL,
    application_name            varchar(255) NOT NULL,
    source_path                 varchar(2048) NOT NULL,
    recognizer_type             recognizer_type NOT NULL,
    task_mode                   task_mode   NOT NULL DEFAULT 'approval_required',
    enabled                     boolean     NOT NULL DEFAULT true,
    priority                    int         NOT NULL DEFAULT 100,
    importance_level            importance_level NOT NULL DEFAULT 'normal',
    scan_schedule               varchar(255),
    upload_window_start         time,
    upload_window_end           time,
    schedule_timezone           varchar(64) NOT NULL DEFAULT 'Asia/Shanghai',
    random_delay_minutes        int         NOT NULL DEFAULT 0,
    stability_interval_seconds  int         NOT NULL DEFAULT 600,
    max_stability_wait_seconds  int         NOT NULL DEFAULT 7200,
    min_total_bytes             bigint,
    max_total_bytes             bigint,
    min_file_count              int,
    bandwidth_limit_kbps        int,
    chunk_size_bytes            int         NOT NULL DEFAULT 8388608,
    retry_count                 int         NOT NULL DEFAULT 3,
    retry_interval_seconds      int         NOT NULL DEFAULT 1800,
    retention_policy_id         UUID,  -- 延迟添加 FK，retention_policies 在后面创建
    config_version              bigint      NOT NULL DEFAULT 1,
    recognizer_config           jsonb       NOT NULL DEFAULT '{}'::jsonb,
    alert_config                jsonb,
    last_scan_at                timestamptz,
    last_precheck_status        precheck_status,
    last_success_at             timestamptz,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    row_version                 bigint      NOT NULL DEFAULT 1,

    CONSTRAINT ck_backup_tasks_chunk_size CHECK (chunk_size_bytes BETWEEN 4194304 AND 33554432),
    CONSTRAINT ck_backup_tasks_priority CHECK (priority BETWEEN 1 AND 1000),
    CONSTRAINT ck_backup_tasks_stability CHECK (stability_interval_seconds >= 60),
    CONSTRAINT ck_backup_tasks_retry CHECK (retry_count >= 0)
);

CREATE INDEX idx_backup_tasks_client_enabled ON backup_tasks (client_id, enabled);
CREATE INDEX idx_backup_tasks_mode_enabled ON backup_tasks (task_mode, enabled);
CREATE INDEX idx_backup_tasks_last_success ON backup_tasks (last_success_at);
CREATE INDEX idx_backup_tasks_application ON backup_tasks (application_name);

COMMENT ON TABLE backup_tasks IS '备份任务配置（核心业务表）';
COMMENT ON COLUMN backup_tasks.recognizer_config IS '识别规则 JSON，结构因 recognizer_type 而异';

-- --------------------------------------------------------------------------
-- 3.16 业务单元表
-- --------------------------------------------------------------------------
CREATE TABLE business_units (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    task_id             UUID        NOT NULL REFERENCES backup_tasks(id) ON DELETE CASCADE,
    external_key        varchar(255) NOT NULL,
    display_name        varchar(255) NOT NULL,
    source_relative_path varchar(2048),
    expected            boolean     NOT NULL DEFAULT true,
    ignored             boolean     NOT NULL DEFAULT false,
    enabled             boolean     NOT NULL DEFAULT true,
    last_discovered_at  timestamptz,
    last_success_at     timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_business_units_task_key UNIQUE (task_id, external_key)
);

CREATE INDEX idx_business_units_task_id ON business_units (task_id);

COMMENT ON TABLE business_units IS '业务单元（账套、数据库等独立对象）';

-- --------------------------------------------------------------------------
-- 3.17 候选备份集表
-- --------------------------------------------------------------------------
CREATE TABLE candidate_backup_sets (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id               UUID        NOT NULL REFERENCES clients(id),
    task_id                 UUID        NOT NULL REFERENCES backup_tasks(id),
    business_unit_id        UUID REFERENCES business_units(id),
    candidate_key           varchar(512) NOT NULL,
    source_root             varchar(2048) NOT NULL,
    backup_business_time    timestamptz,
    discovered_at           timestamptz NOT NULL DEFAULT now(),
    prechecked_at           timestamptz,
    precheck_status         precheck_status NOT NULL DEFAULT 'not_scanned',
    total_files             int,
    total_bytes             bigint,
    manifest_hash           varchar(64),
    quick_fingerprint       varchar(128),
    failure_code            varchar(64),
    failure_message         varchar(2000),
    expires_at              timestamptz,
    superseded_by_id        UUID REFERENCES candidate_backup_sets(id),
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_candidate_sets_task_discovered ON candidate_backup_sets (task_id, discovered_at DESC);
CREATE INDEX idx_candidate_sets_task_bu_time ON candidate_backup_sets (task_id, business_unit_id, backup_business_time DESC);
CREATE INDEX idx_candidate_sets_precheck_status ON candidate_backup_sets (precheck_status);
CREATE INDEX idx_candidate_sets_candidate_key ON candidate_backup_sets (candidate_key);

COMMENT ON TABLE candidate_backup_sets IS '候选备份集（预检阶段产生）';

-- --------------------------------------------------------------------------
-- 3.18 候选文件表
-- --------------------------------------------------------------------------
CREATE TABLE candidate_files (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    candidate_backup_set_id UUID        NOT NULL REFERENCES candidate_backup_sets(id) ON DELETE CASCADE,
    relative_path           varchar(4096) NOT NULL,
    file_name               varchar(1024) NOT NULL,
    size_bytes              bigint      NOT NULL,
    last_modified_at        timestamptz NOT NULL,
    sha256                  varchar(64),
    quick_hash              varchar(64),
    is_required             boolean     NOT NULL DEFAULT false,
    sort_order              int         NOT NULL DEFAULT 0,

    CONSTRAINT uq_candidate_files_set_path UNIQUE (candidate_backup_set_id, relative_path)
);

CREATE INDEX idx_candidate_files_set_id ON candidate_files (candidate_backup_set_id);

-- --------------------------------------------------------------------------
-- 3.19 指令表
-- --------------------------------------------------------------------------
CREATE TABLE commands (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id               UUID        NOT NULL REFERENCES clients(id),
    task_id                 UUID REFERENCES backup_tasks(id),
    candidate_backup_set_id UUID REFERENCES candidate_backup_sets(id),
    command_type            command_type NOT NULL,
    status                  command_status NOT NULL DEFAULT 'pending',
    priority                int         NOT NULL DEFAULT 100,
    payload                 jsonb,
    nonce                   varchar(128) NOT NULL,
    signature               text,
    created_by              UUID REFERENCES users(id),
    created_at              timestamptz NOT NULL DEFAULT now(),
    expires_at              timestamptz NOT NULL,
    claimed_at              timestamptz,
    started_at              timestamptz,
    completed_at            timestamptz,
    result_code             varchar(64),
    result_message          varchar(2000),
    result_payload          jsonb,
    idempotency_key         varchar(128),

    CONSTRAINT uq_commands_nonce UNIQUE (nonce),
    CONSTRAINT uq_commands_idempotency UNIQUE (idempotency_key)
);

CREATE INDEX idx_commands_client_status_priority ON commands (client_id, status, priority, created_at);
CREATE INDEX idx_commands_expires_at ON commands (expires_at);
CREATE INDEX idx_commands_command_type ON commands (command_type);

COMMENT ON TABLE commands IS '服务端下发给客户端的指令队列';

-- --------------------------------------------------------------------------
-- 3.20 上传批次表
-- --------------------------------------------------------------------------
CREATE TABLE upload_batches (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name                        varchar(255),
    created_by                  UUID REFERENCES users(id),
    max_concurrent_clients      int NOT NULL DEFAULT 2,
    max_concurrent_per_client   int NOT NULL DEFAULT 1,
    bandwidth_limit_kbps        int,
    status                      batch_status NOT NULL DEFAULT 'pending',
    total_items                 int NOT NULL DEFAULT 0,
    succeeded_items             int NOT NULL DEFAULT 0,
    failed_items                int NOT NULL DEFAULT 0,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    completed_at                timestamptz
);

COMMENT ON TABLE upload_batches IS '管理员批量下发上传的批次';

-- --------------------------------------------------------------------------
-- 3.21 上传会话表
-- --------------------------------------------------------------------------
CREATE TABLE upload_sessions (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    upload_batch_id         UUID REFERENCES upload_batches(id),
    client_id               UUID        NOT NULL REFERENCES clients(id),
    task_id                 UUID        NOT NULL REFERENCES backup_tasks(id),
    candidate_backup_set_id UUID        NOT NULL REFERENCES candidate_backup_sets(id),
    status                  upload_status NOT NULL DEFAULT 'created',
    total_files             int         NOT NULL,
    total_bytes             bigint      NOT NULL,
    uploaded_bytes          bigint      NOT NULL DEFAULT 0,
    verified_bytes          bigint      NOT NULL DEFAULT 0,
    chunk_size_bytes        int         NOT NULL DEFAULT 8388608,
    staging_path            varchar(2048),
    started_at              timestamptz,
    last_activity_at        timestamptz,
    completed_at            timestamptz,
    verified_at             timestamptz,
    committed_at            timestamptz,
    retry_count             int         NOT NULL DEFAULT 0,
    error_code              varchar(64),
    error_message           varchar(2000),
    idempotency_key         varchar(128),
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_upload_sessions_idempotency UNIQUE (idempotency_key)
);

CREATE INDEX idx_upload_sessions_client_status ON upload_sessions (client_id, status);
CREATE INDEX idx_upload_sessions_task_created ON upload_sessions (task_id, created_at DESC);
CREATE INDEX idx_upload_sessions_candidate ON upload_sessions (candidate_backup_set_id);
CREATE INDEX idx_upload_sessions_last_activity ON upload_sessions (last_activity_at);

COMMENT ON TABLE upload_sessions IS '上传会话（分块上传协议核心表）';

-- --------------------------------------------------------------------------
-- 3.22 上传文件表
-- --------------------------------------------------------------------------
CREATE TABLE upload_files (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    upload_session_id   UUID        NOT NULL REFERENCES upload_sessions(id) ON DELETE CASCADE,
    candidate_file_id   UUID        NOT NULL REFERENCES candidate_files(id),
    relative_path       varchar(4096) NOT NULL,
    size_bytes          bigint      NOT NULL,
    expected_sha256     varchar(64) NOT NULL,
    server_sha256       varchar(64),
    uploaded_bytes      bigint      NOT NULL DEFAULT 0,
    total_chunks        int         NOT NULL,
    uploaded_chunks     int         NOT NULL DEFAULT 0,
    status              upload_file_status NOT NULL DEFAULT 'pending',
    temp_path           varchar(2048),
    error_code          varchar(64),

    CONSTRAINT uq_upload_files_session_path UNIQUE (upload_session_id, relative_path)
);

CREATE INDEX idx_upload_files_session_id ON upload_files (upload_session_id);

-- --------------------------------------------------------------------------
-- 3.23 上传分块表
-- --------------------------------------------------------------------------
CREATE TABLE upload_chunks (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    upload_file_id  UUID        NOT NULL REFERENCES upload_files(id) ON DELETE CASCADE,
    chunk_index     int         NOT NULL,
    offset_bytes    bigint      NOT NULL,
    size_bytes      int         NOT NULL,
    expected_hash   varchar(64) NOT NULL,
    server_hash     varchar(64),
    status          upload_chunk_status NOT NULL DEFAULT 'pending',
    received_at     timestamptz,

    CONSTRAINT uq_upload_chunks_file_index UNIQUE (upload_file_id, chunk_index)
);

CREATE INDEX idx_upload_chunks_file_id ON upload_chunks (upload_file_id);

COMMENT ON TABLE upload_chunks IS '上传分块记录（入库后可清理明细）';

-- --------------------------------------------------------------------------
-- 3.24 正式备份集表
-- --------------------------------------------------------------------------
CREATE TABLE backup_sets (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id               UUID        NOT NULL REFERENCES clients(id),
    task_id                 UUID        NOT NULL REFERENCES backup_tasks(id),
    business_unit_id        UUID REFERENCES business_units(id),
    source_candidate_id     UUID        NOT NULL REFERENCES candidate_backup_sets(id),
    upload_session_id       UUID        NOT NULL REFERENCES upload_sessions(id),
    backup_set_code         varchar(255) NOT NULL,
    status                  backup_set_status NOT NULL DEFAULT 'verifying',
    backup_business_time    timestamptz,
    discovered_at           timestamptz NOT NULL,
    uploaded_at             timestamptz NOT NULL,
    verified_at             timestamptz,
    repository_path         varchar(2048),
    manifest_path           varchar(2048),
    manifest_sha256         varchar(64),
    total_files             int         NOT NULL,
    total_bytes             bigint      NOT NULL,
    locked                  boolean     NOT NULL DEFAULT false,
    retention_until         timestamptz,
    created_at              timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_backup_sets_code UNIQUE (backup_set_code),
    CONSTRAINT uq_backup_sets_candidate UNIQUE (source_candidate_id)  -- 一个候选只能产生一个正式版本
);

CREATE INDEX idx_backup_sets_task_bu_time ON backup_sets (task_id, business_unit_id, backup_business_time DESC);
CREATE INDEX idx_backup_sets_client_created ON backup_sets (client_id, created_at DESC);
CREATE INDEX idx_backup_sets_status ON backup_sets (status);
CREATE INDEX idx_backup_sets_retention ON backup_sets (retention_until);

COMMENT ON TABLE backup_sets IS '正式备份版本（校验入库后生成）';

-- --------------------------------------------------------------------------
-- 3.25 正式备份文件表
-- --------------------------------------------------------------------------
CREATE TABLE backup_files (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    backup_set_id               UUID        NOT NULL REFERENCES backup_sets(id) ON DELETE CASCADE,
    relative_path               varchar(4096) NOT NULL,
    file_name                   varchar(1024) NOT NULL,
    size_bytes                  bigint      NOT NULL,
    last_modified_at            timestamptz NOT NULL,
    sha256                      varchar(64) NOT NULL,
    repository_relative_path    varchar(4096) NOT NULL,
    verification_status         verification_status NOT NULL DEFAULT 'verified',
    created_at                  timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_backup_files_set_path UNIQUE (backup_set_id, relative_path)
);

CREATE INDEX idx_backup_files_set_id ON backup_files (backup_set_id);

-- --------------------------------------------------------------------------
-- 3.26 保留策略表
-- --------------------------------------------------------------------------
CREATE TABLE retention_policies (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name                    varchar(128) NOT NULL,
    keep_last_count         int,
    keep_weekly_count       int,
    keep_monthly_count      int,
    keep_yearly_count       int,
    minimum_retention_days  int         NOT NULL DEFAULT 30,
    recycle_bin_days        int         NOT NULL DEFAULT 30,
    config                  jsonb,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT ck_retention_policies_counts CHECK (
        keep_last_count IS NULL OR keep_last_count > 0
    )
);

COMMENT ON TABLE retention_policies IS '备份保留策略';

-- 补充 backup_tasks 的 FK
ALTER TABLE backup_tasks
    ADD CONSTRAINT fk_backup_tasks_retention_policy
    FOREIGN KEY (retention_policy_id) REFERENCES retention_policies(id);

-- --------------------------------------------------------------------------
-- 3.27 保留锁表
-- --------------------------------------------------------------------------
CREATE TABLE retention_locks (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    backup_set_id   UUID        NOT NULL REFERENCES backup_sets(id) ON DELETE CASCADE,
    reason          varchar(1000) NOT NULL,
    locked_by       UUID        NOT NULL REFERENCES users(id),
    locked_at       timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz,
    active          boolean     NOT NULL DEFAULT true
);

CREATE INDEX idx_retention_locks_backup_set ON retention_locks (backup_set_id);
CREATE INDEX idx_retention_locks_active ON retention_locks (active) WHERE active = true;

COMMENT ON TABLE retention_locks IS '备份版本保留锁（阻止清理）';

-- --------------------------------------------------------------------------
-- 3.28 恢复请求表
-- --------------------------------------------------------------------------
CREATE TABLE restore_requests (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    backup_set_id           UUID        NOT NULL REFERENCES backup_sets(id),
    requested_by            UUID        NOT NULL REFERENCES users(id),
    purpose                 varchar(1000) NOT NULL,
    status                  restore_request_status NOT NULL DEFAULT 'requested',
    requested_at            timestamptz NOT NULL DEFAULT now(),
    verified_at             timestamptz,
    download_token_hash     varchar(255),
    download_expires_at     timestamptz,
    downloaded_bytes        bigint      NOT NULL DEFAULT 0,
    completed_at            timestamptz,
    client_ip               varchar(64),
    error_message           varchar(2000)
);

CREATE INDEX idx_restore_requests_backup_set ON restore_requests (backup_set_id);
CREATE INDEX idx_restore_requests_requested_by ON restore_requests (requested_by);

COMMENT ON TABLE restore_requests IS '恢复下载请求记录';

-- --------------------------------------------------------------------------
-- 3.29 告警表
-- --------------------------------------------------------------------------
CREATE TABLE alerts (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    alert_key           varchar(255) NOT NULL,
    level               alert_level NOT NULL,
    status              alert_status NOT NULL DEFAULT 'open',
    category            varchar(64),
    client_id           UUID REFERENCES clients(id),
    task_id             UUID REFERENCES backup_tasks(id),
    business_unit_id    UUID REFERENCES business_units(id),
    backup_set_id       UUID REFERENCES backup_sets(id),
    title               varchar(255) NOT NULL,
    message             varchar(4000),
    first_occurred_at   timestamptz NOT NULL DEFAULT now(),
    last_occurred_at    timestamptz NOT NULL DEFAULT now(),
    occurrence_count    int NOT NULL DEFAULT 1,
    acknowledged_by     UUID REFERENCES users(id),
    acknowledged_at     timestamptz,
    recovered_at        timestamptz,
    closed_at           timestamptz,
    handling_note       varchar(4000),
    metadata            jsonb
);

CREATE INDEX idx_alerts_level_status ON alerts (level, status);
CREATE INDEX idx_alerts_client ON alerts (client_id);
CREATE INDEX idx_alerts_task ON alerts (task_id);
CREATE INDEX idx_alerts_last_occurred ON alerts (last_occurred_at DESC);
-- 活动告警唯一约束（同一 alert_key 在活动状态下只能有一条）
CREATE UNIQUE INDEX uq_alerts_active_key
    ON alerts (alert_key)
    WHERE status IN ('open', 'acknowledged', 'in_progress');

COMMENT ON TABLE alerts IS '系统告警（支持去重和状态流转）';

-- --------------------------------------------------------------------------
-- 3.30 通知记录表
-- --------------------------------------------------------------------------
CREATE TABLE notification_deliveries (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    alert_id        UUID REFERENCES alerts(id) ON DELETE SET NULL,
    channel         notification_channel NOT NULL,
    recipient       varchar(255) NOT NULL,
    status          notification_status NOT NULL DEFAULT 'pending',
    attempt_count   int NOT NULL DEFAULT 0,
    last_attempt_at timestamptz,
    sent_at         timestamptz,
    error_message   varchar(2000)
);

CREATE INDEX idx_notification_deliveries_alert ON notification_deliveries (alert_id);
CREATE INDEX idx_notification_deliveries_status ON notification_deliveries (status);

-- --------------------------------------------------------------------------
-- 3.31 审计日志表（按月分区，只追加不修改）
-- --------------------------------------------------------------------------
CREATE TABLE audit_logs (
    id                  UUID NOT NULL DEFAULT gen_random_uuid(),
    occurred_at         timestamptz NOT NULL DEFAULT now(),
    user_id             UUID,
    username_snapshot   varchar(128),
    client_ip           varchar(64),
    action              varchar(128) NOT NULL,
    resource_type       varchar(64),
    resource_id         UUID,
    request_id          varchar(128),
    result              audit_result NOT NULL,
    before_data         jsonb,
    after_data          jsonb,
    error_code          varchar(64),
    error_message       varchar(2000),
    user_agent          varchar(512),
    metadata            jsonb,

    CONSTRAINT pk_audit_logs PRIMARY KEY (id, occurred_at)
) PARTITION BY RANGE (occurred_at);

CREATE INDEX idx_audit_logs_occurred ON audit_logs (occurred_at DESC);
CREATE INDEX idx_audit_logs_user_occurred ON audit_logs (user_id, occurred_at DESC);
CREATE INDEX idx_audit_logs_resource ON audit_logs (resource_type, resource_id);
CREATE INDEX idx_audit_logs_action ON audit_logs (action);
CREATE INDEX idx_audit_logs_request_id ON audit_logs (request_id);

COMMENT ON TABLE audit_logs IS '审计日志（只追加，不可通过普通 API 修改或删除）';

-- 创建初始分区
CREATE TABLE audit_logs_y2026m07
    PARTITION OF audit_logs
    FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
CREATE TABLE audit_logs_y2026m08
    PARTITION OF audit_logs
    FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');
CREATE TABLE audit_logs_y2026m09
    PARTITION OF audit_logs
    FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');
CREATE TABLE audit_logs_y2026m10
    PARTITION OF audit_logs
    FOR VALUES FROM ('2026-10-01') TO ('2026-11-01');
CREATE TABLE audit_logs_y2026m11
    PARTITION OF audit_logs
    FOR VALUES FROM ('2026-11-01') TO ('2026-12-01');
CREATE TABLE audit_logs_y2026m12
    PARTITION OF audit_logs
    FOR VALUES FROM ('2026-12-01') TO ('2027-01-01');

-- --------------------------------------------------------------------------
-- 3.32 系统配置表
-- --------------------------------------------------------------------------
CREATE TABLE system_settings (
    setting_key     varchar(128) PRIMARY KEY,
    setting_value   jsonb       NOT NULL,
    encrypted       boolean     NOT NULL DEFAULT false,
    updated_by      UUID REFERENCES users(id),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    row_version     bigint      NOT NULL DEFAULT 1
);

COMMENT ON TABLE system_settings IS '系统配置（敏感配置必须应用层加密）';


-- ============================================================================
-- 4. 种子数据
-- ============================================================================

-- 4.1 内置角色
INSERT INTO roles (id, code, name, description, is_system) VALUES
    ('a0000001-0000-0000-0000-000000000001', 'system_admin',    '系统管理员', '拥有全部权限，管理用户和系统配置', true),
    ('a0000001-0000-0000-0000-000000000002', 'backup_admin',    '备份管理员', '管理备份任务、下发预检和上传', true),
    ('a0000001-0000-0000-0000-000000000003', 'restore_operator','恢复操作员', '执行备份恢复下载操作', true),
    ('a0000001-0000-0000-0000-000000000004', 'auditor',         '审计员',     '查看审计日志和报表', true);

-- 4.2 权限定义
INSERT INTO permissions (id, code, name, module, description) VALUES
    -- 客户端管理
    ('b0000001-0000-0000-0000-000000000001', 'clients.read',       '查看客户端',     'clients',  '查看客户端列表和详情'),
    ('b0000001-0000-0000-0000-000000000002', 'clients.manage',     '管理客户端',     'clients',  '审批、禁用、注销客户端'),
    -- 任务管理
    ('b0000001-0000-0000-0000-000000000003', 'tasks.read',         '查看任务',       'tasks',    '查看备份任务列表和详情'),
    ('b0000001-0000-0000-0000-000000000004', 'tasks.manage',       '管理任务',       'tasks',    '创建、编辑、删除备份任务'),
    ('b0000001-0000-0000-0000-000000000005', 'tasks.precheck',     '下发预检',       'tasks',    '下发预检指令'),
    ('b0000001-0000-0000-0000-000000000006', 'tasks.upload',       '下发上传',       'tasks',    '下发上传指令'),
    -- 备份管理
    ('b0000001-0000-0000-0000-000000000007', 'backups.read',       '查看备份',       'backups',  '查看备份记录和文件清单'),
    ('b0000001-0000-0000-0000-000000000008', 'backups.download',   '下载备份',       'backups',  '创建恢复请求和下载'),
    ('b0000001-0000-0000-0000-000000000009', 'backups.manage',     '管理备份',       'backups',  '锁定、解锁、校验备份'),
    -- 告警
    ('b0000001-0000-0000-0000-000000000010', 'alerts.read',        '查看告警',       'alerts',   '查看告警列表'),
    ('b0000001-0000-0000-0000-000000000011', 'alerts.handle',      '处理告警',       'alerts',   '确认、处理、关闭告警'),
    -- 审计
    ('b0000001-0000-0000-0000-000000000012', 'audit.read',         '查看审计日志',   'audit',    '查看审计日志'),
    -- 系统管理
    ('b0000001-0000-0000-0000-000000000013', 'system.manage',      '系统管理',       'system',   '管理系统配置、用户、角色'),
    -- 操作中心
    ('b0000001-0000-0000-0000-000000000014', 'operations.batch',   '批量操作',       'operations','创建批量预检和上传'),
    -- 分组管理
    ('b0000001-0000-0000-0000-000000000015', 'groups.read',        '查看分组',       'groups',   '查看客户端分组'),
    ('b0000001-0000-0000-0000-000000000016', 'groups.manage',      '管理分组',       'groups',   '创建、编辑分组');

-- 4.3 角色权限分配
-- 系统管理员：全部权限
INSERT INTO role_permissions (role_id, permission_id)
SELECT 'a0000001-0000-0000-0000-000000000001', id FROM permissions;

-- 备份管理员
INSERT INTO role_permissions (role_id, permission_id) VALUES
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000001'), -- clients.read
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000003'), -- tasks.read
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000004'), -- tasks.manage
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000005'), -- tasks.precheck
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000006'), -- tasks.upload
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000007'), -- backups.read
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000009'), -- backups.manage
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000010'), -- alerts.read
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000011'), -- alerts.handle
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000014'), -- operations.batch
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000015'), -- groups.read
    ('a0000001-0000-0000-0000-000000000002', 'b0000001-0000-0000-0000-000000000016'); -- groups.manage

-- 恢复操作员
INSERT INTO role_permissions (role_id, permission_id) VALUES
    ('a0000001-0000-0000-0000-000000000003', 'b0000001-0000-0000-0000-000000000001'), -- clients.read
    ('a0000001-0000-0000-0000-000000000003', 'b0000001-0000-0000-0000-000000000003'), -- tasks.read
    ('a0000001-0000-0000-0000-000000000003', 'b0000001-0000-0000-0000-000000000007'), -- backups.read
    ('a0000001-0000-0000-0000-000000000003', 'b0000001-0000-0000-0000-000000000008'), -- backups.download
    ('a0000001-0000-0000-0000-000000000003', 'b0000001-0000-0000-0000-000000000015'); -- groups.read

-- 审计员
INSERT INTO role_permissions (role_id, permission_id) VALUES
    ('a0000001-0000-0000-0000-000000000004', 'b0000001-0000-0000-0000-000000000001'), -- clients.read
    ('a0000001-0000-0000-0000-000000000004', 'b0000001-0000-0000-0000-000000000003'), -- tasks.read
    ('a0000001-0000-0000-0000-000000000004', 'b0000001-0000-0000-0000-000000000007'), -- backups.read
    ('a0000001-0000-0000-0000-000000000004', 'b0000001-0000-0000-0000-000000000010'), -- alerts.read
    ('a0000001-0000-0000-0000-000000000004', 'b0000001-0000-0000-0000-000000000012'), -- audit.read
    ('a0000001-0000-0000-0000-000000000004', 'b0000001-0000-0000-0000-000000000015'); -- groups.read

-- 4.4 默认管理员账户：已移至 V005__admin_password_bootstrap.sql。
--     口令不再硬编码于版本库，由 dbinit 部署时经 ADMIN_PW 注入，
--     且初始账户带 must_change_password=true 强制首次改密。

-- 4.5 默认系统配置
INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_by) VALUES
    ('heartbeat_interval_seconds',    '60'::jsonb,              false, NULL),
    ('max_concurrent_uploads',        '2'::jsonb,               false, NULL),
    ('client_offline_threshold_seconds','300'::jsonb,           false, NULL),
    ('client_suspected_offline_seconds','180'::jsonb,           false, NULL),
    ('upload_session_timeout_seconds',  '3600'::jsonb,          false, NULL),
    ('staging_cleanup_days',            '7'::jsonb,             false, NULL),
    ('max_login_attempts',              '5'::jsonb,             false, NULL),
    ('lockout_duration_seconds',        '900'::jsonb,           false, NULL),
    ('access_token_ttl_seconds',        '3600'::jsonb,          false, NULL),
    ('refresh_token_ttl_days',          '7'::jsonb,             false, NULL),
    ('max_page_size',                   '200'::jsonb,           false, NULL),
    ('default_page_size',               '50'::jsonb,            false, NULL),
    ('max_single_file_bytes',           '1099511627776'::jsonb, false, NULL), -- 1TB
    ('max_files_per_session',           '100000'::jsonb,        false, NULL),
    ('max_path_length',                 '2048'::jsonb,          false, NULL),
    ('repository_path',                 '"E:\\BackupRepository"'::jsonb, false, NULL),
    ('staging_path',                    '"D:\\BackupStaging"'::jsonb,   false, NULL);

-- 4.6 默认保留策略
INSERT INTO retention_policies (id, name, keep_last_count, keep_weekly_count, keep_monthly_count, keep_yearly_count, minimum_retention_days, recycle_bin_days)
VALUES (
    'd0000001-0000-0000-0000-000000000001',
    '默认策略（8周+6月+2年）',
    8,   -- 最近8个周版本
    8,   -- 8个周版本
    6,   -- 6个月末版本
    2,   -- 2个年度版本
    30,  -- 最短保留30天
    30   -- 回收区保留30天
);

-- 4.7 内置任务模板
INSERT INTO backup_task_templates (code, name, recognizer_type, default_config, is_system) VALUES
    ('tpl_latest_file', '最新单文件', 'latest_single_file',
     '{"includePatterns":["*"],"excludePatterns":["*.tmp","*.partial"],"recursive":false,"sortBy":"lastModified"}'::jsonb, true),
    ('tpl_latest_dir', '最新目录', 'latest_directory',
     '{"includePatterns":["*"],"excludePatterns":["*.tmp","*.partial"],"recursive":true,"sortBy":"lastModified"}'::jsonb, true),
    ('tpl_multi_file', '多文件备份集', 'multi_file_set',
     '{"includePatterns":["*.bak","*.dif","*.trn"],"excludePatterns":["*.tmp"],"recursive":false,"sortBy":"lastModified","requiredFiles":[],"batchRegex":null}'::jsonb, true),
    ('tpl_subdir_units', '多子目录/多账套', 'subdirectory_units',
     '{"recursive":true,"businessUnitDepth":1,"excludeDirectories":["Temp","Old"],"requiredFiles":[]}'::jsonb, true);


-- ============================================================================
-- 5. 常用函数
-- ============================================================================

-- 5.1 自动更新 updated_at
CREATE OR REPLACE FUNCTION fn_update_timestamp()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- 为所有包含 updated_at 的表创建触发器
CREATE TRIGGER trg_users_updated
    BEFORE UPDATE ON users FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_roles_updated
    BEFORE UPDATE ON roles FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_client_groups_updated
    BEFORE UPDATE ON client_groups FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_clients_updated
    BEFORE UPDATE ON clients FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_backup_task_templates_updated
    BEFORE UPDATE ON backup_task_templates FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_backup_tasks_updated
    BEFORE UPDATE ON backup_tasks FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_business_units_updated
    BEFORE UPDATE ON business_units FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_candidate_sets_updated
    BEFORE UPDATE ON candidate_backup_sets FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_upload_sessions_updated
    BEFORE UPDATE ON upload_sessions FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_retention_policies_updated
    BEFORE UPDATE ON retention_policies FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();
CREATE TRIGGER trg_system_settings_updated
    BEFORE UPDATE ON system_settings FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();

-- 5.2 乐观锁检查函数（在应用层使用，这里提供参考）
-- 应用层 UPDATE 时应包含: WHERE id = ? AND row_version = ?
-- 并在 SET 中: row_version = row_version + 1


COMMIT;

-- ============================================================================
-- 脚本结束
-- 总计: 32 张表 + 10 个核心枚举 + 辅助枚举 + 索引 + 种子数据 + 触发器
-- ============================================================================
