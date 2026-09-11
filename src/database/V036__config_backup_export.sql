-- V036 配置备份包的远程入口（整改清单 2026-09-10 · R4）
--
-- 配置备份包（.bmbp）= server-secrets.json + client-ca.pfx + server-certificate.pfx + 整库转储，
-- 等于整个系统的命：它丢了，仓库里的备份文件即使还在，
-- 「哪个文件属于哪台机器、哪个任务、哪一天」也全没了，同时所有 Agent 身份作废。
--
-- 在此之前唯一的导出入口是服务管理台的按钮——必须有人坐到服务器前面点。
-- 这张表让管理网页也能触发导出、看到上次结果、并按一次性令牌把包取走。
--
-- 注意这张表**不是**「上次导出时间」的权威来源：服务管理台本机导出的那些包不经过 API，
-- 不会在这里留行。权威来源是数据目录旁 config-backups 目录里最新的那个文件
--（见 ConfigBackupPaths）。这张表记的是「通过管理网页发起的那些尝试」，
-- 它的价值在于留下失败原因和承载下载令牌。

CREATE TABLE IF NOT EXISTS config_backup_exports (
    id                  uuid PRIMARY KEY,
    -- running / succeeded / failed，与 EnumMapping 的 snake_case 一致
    status              varchar(20) NOT NULL,
    file_path           text NULL,
    file_name           varchar(255) NULL,
    size_bytes          bigint NULL,
    error_message       text NULL,
    started_at          timestamptz NOT NULL,
    completed_at        timestamptz NULL,
    requested_by        uuid NULL REFERENCES users(id) ON DELETE SET NULL,
    requested_by_name   varchar(100) NULL,
    -- 与恢复下载同一套口径：库里只存哈希，明文令牌只在签发响应里出现一次。
    -- 包内有 CA 私钥，是全系统最敏感的文件，不能挂在一个长期有效的地址上。
    download_token_hash varchar(255) NULL,
    download_expires_at timestamptz NULL,
    created_at          timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE config_backup_exports IS
    '通过管理网页发起的配置备份包导出尝试；上次导出时间的权威来源是 config-backups 目录本身';

CREATE INDEX IF NOT EXISTS idx_config_backup_exports_started
    ON config_backup_exports (started_at DESC);

-- 令牌哈希唯一：换发即失效旧令牌，两行拿到同一个哈希是不该出现的状态。
CREATE UNIQUE INDEX IF NOT EXISTS uq_config_backup_exports_token
    ON config_backup_exports (download_token_hash)
    WHERE download_token_hash IS NOT NULL;
