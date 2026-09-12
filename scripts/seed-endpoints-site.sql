-- 本站点业务探测预置（一次性脚本，不是产品迁移）
--
-- 刻意不做成 V0xx 迁移：迁移会在任何一次全新安装时执行，把这几个 172.16.11.x
-- 的地址写死进产品里，以后装到别的现场就会莫名其妙多出十条连不上的探测。
--
-- 用法：在服务端数据库上执行一次。重复执行安全（按名称去重）。
--
-- 客户端关联按 display_name 匹配。匹配不上就留空——留空只影响告警归属和维护静默，
-- 探测本身照常工作，事后在界面上补选即可。脚本末尾会列出没匹配上的。

INSERT INTO monitored_endpoints
    (name, client_id, probe_type, target, port, expected_status, expected_content,
     timeout_seconds, interval_seconds, failure_threshold, enabled, alert_on_failure)
SELECT v.name,
       (SELECT c.id FROM clients c WHERE c.display_name = v.client_name LIMIT 1),
       v.probe_type, v.target, v.port, v.expected_status, NULL,
       v.timeout_seconds, v.interval_seconds, 3, true, true
FROM (VALUES
    -- ── 用友 U8 ────────────────────────────────────────────────────────
    -- 4630 加密服务最该探：加密狗掉了业务全停，而应用服务和 SQL Server
    -- 全都显示正常——服务状态监控对这种故障一个字都不会说。
    ('恒源U8 加密服务',   '恒源_U8服务器', 'tcp',  '172.16.11.83',  4630,  NULL, 10, 60),
    -- 11520 是 U8DispatchService，UAP 和登录的远程代理，它挂了谁都登不进去。
    ('恒源U8 登录代理',   '恒源_U8服务器', 'tcp',  '172.16.11.83',  11520, NULL, 10, 60),
    ('恒源U8 数据库',     '恒源_U8服务器', 'tcp',  '172.16.11.83',  1433,  NULL, 10, 60),

    ('瑞来U8 加密服务',   '瑞来_U8服务器', 'tcp',  '172.16.11.141', 4630,  NULL, 10, 60),
    ('瑞来U8 登录代理',   '瑞来_U8服务器', 'tcp',  '172.16.11.141', 11520, NULL, 10, 60),
    ('瑞来U8 数据库',     '瑞来_U8服务器', 'tcp',  '172.16.11.141', 1433,  NULL, 10, 60),

    -- ── 大宗物料 ──────────────────────────────────────────────────────
    -- Nginx 8008 是用户的大门，这一条顺着 Nginx → Tomcat → Redis → SQL Server
    -- 整条链走一遍，一条顶四条。Redis 绑在 127.0.0.1，服务端本来也探不到它。
    ('大宗物料 应用入口', '瑞来_大宗物料服务器', 'http', 'http://172.16.11.139:8008/', NULL, '200,302', 10, 300),
    -- 大门坏了的时候，用这条分辨是 Nginx 的事还是 Tomcat 的事。
    ('大宗物料 Tomcat',   '瑞来_大宗物料服务器', 'tcp',  '172.16.11.139', 8004, NULL, 10, 60),

    -- ── 致远 OA ───────────────────────────────────────────────────────
    -- 间隔放到 300 秒：登录页每次访问都可能建 session、写审计日志，
    -- 一分钟一次就是一天 1440 次，应用日志会被刷爆。
    ('恒源OA 应用',       '恒源_OA服务器', 'http', 'http://172.16.11.175:8088/seeyon/index.jsp', NULL, '200,302', 15, 300),
    ('瑞来OA 应用',       '瑞来_OA服务器', 'http', 'http://172.16.11.229:8080/seeyon/index.jsp', NULL, '200,302', 15, 300)
) AS v(name, client_name, probe_type, target, port, expected_status, timeout_seconds, interval_seconds)
WHERE NOT EXISTS (SELECT 1 FROM monitored_endpoints e WHERE e.name = v.name);

-- 三条 HTTP 的「期望包含文本」刻意留空，必须人工补。
--
-- 它是 HTTP 探测里唯一真正管用的一项：只看状态码基本没用，Tomcat 的错误页、
-- 应用的「系统维护中」页返回的都是 200。而填什么词只能看真实响应才知道——
-- 猜一个填进去，万一错误页里也有，这条探测就永远是绿的，而人以为它在把关。
--
-- 补法：业务探测页 → 编辑那三条 → 点「立即试一次」→ 从回显的响应正文里挑一个
-- 只有正常页面才有的词（比如登录表单的字段名）→ 填进「期望包含文本」→ 保存。

SELECT e.name,
       e.probe_type,
       e.target || COALESCE(':' || e.port::text, '') AS 目标,
       CASE WHEN e.client_id IS NULL THEN '⚠ 没匹配上客户端，请到界面上补选' ELSE '已关联' END AS 客户端,
       CASE WHEN e.probe_type = 'http' AND e.expected_content IS NULL
            THEN '⚠ 待补「期望包含文本」' ELSE '' END AS 待办
FROM monitored_endpoints e
ORDER BY e.name;
