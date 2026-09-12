-- V047 业务系统探测（TCP / HTTP）
--
-- 被监控的 Windows 服务（monitored_service_definitions）回答的是「进程在不在」，
-- 而这类系统最常见的故障形态恰恰是**进程好好的、业务已经用不了**：
-- Tomcat 活着但 webapp 已经 OOM、加密狗掉了、数据库连接池耗尽。
-- 服务状态对这些一律显示正常。
--
-- 探测补的是这一层：直接连业务端口 / 打业务 URL，用和真实用户相同的路径去验。
--
-- ── 为什么从服务端探，而不是让 Agent 探本机回环 ──────────────────────
-- 值得探的那些端口本来就是给客户端连的：U8 的 11520（登录代理，它挂了谁都登不进去）、
-- 4630（加密服务，狗掉了业务全停而应用服务毫无异常）、1433，致远的 HTTP 端口。
-- 从服务端探走的是和用户完全相同的一条路；探 localhost 反而测得更少——
-- 本机通不代表别人连得上。
--
-- 代价是分不清「应用死了」和「网络断了」。这几台都在同一个网段，
-- 网络真断了备份也做不了，会从别的地方冒出告警来，可以接受。
--
-- 附带好处：不用改客户端、不用下发配置、不用升版本，连没装 Agent 的东西
-- （交换机、NAS）也能探。
CREATE TABLE IF NOT EXISTS monitored_endpoints (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),

    name                varchar(128) NOT NULL,

    -- 关联客户端：告警挂到它名下，在客户端详情里也看得到。
    -- 可以为空——探测的目标不一定装了 Agent。
    client_id           uuid NULL REFERENCES clients(id) ON DELETE SET NULL,

    -- tcp / http
    probe_type          varchar(16) NOT NULL DEFAULT 'tcp',

    -- tcp: 主机名或 IP；http: 完整 URL
    target              varchar(1024) NOT NULL,

    -- tcp 才用
    port                int NULL,

    -- http 才用。期望状态码逗号分隔（登录页常会 302，所以要能填多个）。
    expected_status     varchar(64) NULL,

    -- http 才用，而且是这个功能的成败所在：
    -- 只看状态码基本没用——Tomcat 的错误页、应用的「系统维护中」页返回的都是 200。
    -- 真正区分「HTTP 通了」和「应用还活着」的，是响应里有没有那段只有正常页面才有的文本。
    expected_content    varchar(512) NULL,

    timeout_seconds     int NOT NULL DEFAULT 10,

    interval_seconds    int NOT NULL DEFAULT 60,

    -- 连续失败几次才报。一次抖动就报警，练几次人就麻木了，
    -- 而麻木之后连真出事的那条也会被一起划走。
    failure_threshold   int NOT NULL DEFAULT 3,

    -- 响应慢于这么多毫秒也算异常（http，可空表示不判）。
    -- 从 200ms 变成 8 秒是最早的预警，而那时状态码还是 200。
    slow_milliseconds   int NULL,

    enabled             boolean NOT NULL DEFAULT true,
    alert_on_failure    boolean NOT NULL DEFAULT true,

    -- 运行期状态
    last_probed_at      timestamptz NULL,
    last_success_at     timestamptz NULL,
    last_status         varchar(16) NULL,          -- up / down
    last_latency_ms     int NULL,
    last_error          varchar(512) NULL,
    consecutive_failures int NOT NULL DEFAULT 0,

    created_by          uuid NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_monitored_endpoints_client
    ON monitored_endpoints (client_id);

CREATE INDEX IF NOT EXISTS idx_monitored_endpoints_due
    ON monitored_endpoints (enabled, last_probed_at);

COMMENT ON TABLE monitored_endpoints IS
    '业务系统探测：直接连端口或打 URL，验「业务用不用得了」而不只是「进程在不在」（V047）';

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
VALUES
    -- 探测工作器多久醒一次（秒）。每条探测有自己的间隔，这个只是轮询节奏。
    ('endpoint_probe_tick_seconds', '20'::jsonb, false, now())
ON CONFLICT (setting_key) DO NOTHING;
