# 修复提示词集（对应 REVIEW-2026-08-13.md）

交给 AI 编码助手执行。**每次只跑一个批次**，每个批次前先贴一遍「共通前置」。
跑完一个批次先 review 改动、跑构建与测试，确认无回归再跑下一个。

---

## 【共通前置】

```
## 项目背景

BackupMonitor：局域网内部使用的轻量级集中备份采集与监控系统，包含服务端、Windows Agent、
托盘程序和两个双击安装器。

技术栈与结构：
- .NET 8 + PostgreSQL + EF Core（Npgsql），解决方案 src/BackupMonitor.sln 共 9 个项目
- 服务端四层：src/src/BackupMonitor.{Api,Infrastructure,Core,Shared}
- 客户端：src/agent/BackupMonitor.{Agent,Agent.Tray,Agent.Setup}
- 服务端安装器：src/server/BackupMonitor.Server.Setup（内置 PostgreSQL payload）
- 数据库迁移：src/database/V001~V008__*.sql，由安装器的 MigrationRunner 顺序执行并记 checksum
- 管理控制台：wwwroot 下原生 ES module + 三个 CSS 文件，零构建、零外部依赖
- 管理端 JWT + 16 个权限策略；Agent 端客户端证书（mTLS）认证
- 两种部署形态：
  * Secure：nginx 终结 TLS/mTLS，反向代理到 Kestrel 127.0.0.1:5080
  * LanSimple（Turnkey）：安装器一键部署，内置专用 PostgreSQL 实例，免令牌自动登记

## 硬约束

1. 代码注释与用户可见文案一律中文；注释中用「设计书 X.Y」引用需求文档章节。
2. 匹配现有代码风格：现有命名习惯、BusinessException / ApiResponse<T> 异常与响应封装、
   EnumMapping 的 snake_case 转换、AsNoTracking + 投影的查询写法。
3. 机密（数据库口令、JWT 密钥、签名私钥、CA 口令）绝不写入任何 appsettings*.json、源码、
   日志、接口响应或客户端安装包。
4. 不新增第三方依赖，除非提示词里明确允许。
5. 不扩大范围。只做当前批次明确要求的事。发现别的问题记下来，在报告末尾列出，不要顺手改。
6. 不要执行任何 git 操作（commit / push / reset / checkout / clean / stash）。版本控制由项目主人自己管理，
   与你无关。也不要在报告里讨论仓库管理、.gitignore 或代码托管平台。
7. 不得为了修复而删除已有的 mTLS、指令签名、哈希校验、路径安全、审计或数据库事务边界。
8. 每个批次开始前先跑通 `dotnet build src/BackupMonitor.sln -c Release`，
   有编译错误先修掉并在报告中单独说明。

## 参考文档

- docs/REVIEW-2026-08-13.md —— 问题清单与修复方案（本次工作依据，编号 #1~#22）
- docs/LAN-TURNKEY-CHANGE-PLAN.md —— 局域网一键交付的原始设计约束
- docs/LAN-TURNKEY-ACCEPTANCE.md —— 上一轮的真实完成状态（批次 G 端到端验收未执行）
- deploy/README.md —— Secure 形态的部署说明

## 报告格式

每批完成后输出：
1. 实际改动的文件清单
2. 用户可见的行为变化
3. 执行过的构建/测试命令及退出码（失败就写失败，不许写成通过）
4. 未完成项与真实阻塞原因
5. 是否影响 Secure 部署形态
6. 顺带发现但未处理的问题
```

---

## 批次 1 · Agent 认证与传输安全（最优先，#1 + #5）

```
读 docs/REVIEW-2026-08-13.md 的 #1 和 #5。

这是最严重的问题：LAN Turnkey 形态下 Agent 根本无法通过认证。服务端安装器写死监听明文 HTTP
（ServerInstaller.cs 的 appsettings.Turnkey.json 模板，"http://0.0.0.0:5080"），而全部 Agent 业务端点
要求客户端证书认证方案。明文 HTTP 没有 TLS 握手，客户端证书永远发不出去；LocalServerBootstrap 又把
开发回退头 AllowDevelopmentHeader 强制关成 false。结果：注册能成功，之后心跳、配置、指令、预检、
上传全部 401。

第一步（务必先做）：先按 REVIEW 文档末尾的「最小验证动作」实测确认这个结论，把请求与响应贴进报告。
如果实测结果与结论不符，立刻停下来汇报，不要按错误前提改代码。

确认后按方案 A 实施：

1. 服务端安装器生成自签服务器证书：
   - SAN 覆盖机器名、FQDN 和全部本机局域网 IPv4 地址
   - 存放在 %ProgramData%\BackupMonitor\Server，与 server-secrets.json 同级同 ACL
   - 有效期 2 年；维护模式增加「重新签发服务器证书」入口
2. appsettings.Turnkey.json 模板改为 HTTPS 端点。Kestrel 已配置
   ClientCertificateMode.AllowCertificate，认证处理器无需改动。
3. GET /api/v1/agent/bootstrap 增加返回服务器证书指纹和根证书 PEM（公开信息，不含私钥）。
4. Agent 安装器把指纹写入 Agent 配置；AgentApiClient 用
   ServerCertificateCustomValidationCallback 做指纹固定校验，绝不使用
   DangerouslyAcceptAnyServerCertificateValidator（AllowInsecureTls 保留但默认 false，仅供开发）。
5. 新增 SecurityHeadersMiddleware，放在 RequestIdMiddleware 之后、静态文件之前，写入
   X-Content-Type-Options: nosniff、Referrer-Policy: no-referrer、
   Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'
   —— 注意此时先不要加 script-src 'self'，前端还有大量内联 onclick（批次 6 处理），加了会白屏。
6. 安装完成页与 docs/局域网部署使用说明.md 补充「导入根证书」的操作说明。

不要做：不要改 ClientCertificateAuthenticationHandler 的认证逻辑；不要动 Secure 形态的 nginx 配置；
不要引入 ACME/Let's Encrypt（局域网环境不适用）。

验收：
- 干净环境安装后，Agent 完成注册 → 心跳 → 配置同步 → 预检 → 分块上传全链路 200
- 不带客户端证书、只带 X-Client-Id 头的请求必须 401
- 服务端日志中的 client_id 来自证书指纹查库结果，而非请求头
- Secure 形态（nginx 终结 mTLS）不回归
```

---

## 批次 2 · 分区兜底与遥测清理（#2 + #3）

```
读 docs/REVIEW-2026-08-13.md 的 #2 和 #3。

#2 是有明确到期日的故障：client_heartbeats 和 audit_logs 都是 RANGE 分区，V001 只预建到
2027-01-01，没有 DEFAULT 分区，也没有任何创建分区的代码（注释里提到的「数据库维护任务」不存在）。
2027-01-01 起心跳插入直接失败导致所有客户端显示离线，审计写入失败但异常被吞掉从而静默停止。

#3 是持续积累的问题：client_service_states 每次心跳每个服务插一行且永不删除
（1 客户端 × 5 服务 × 60 秒心跳约 7200 行/天/客户端），client_heartbeats 注释写保留 30 天但无实现，
agent_notifications 有 expires_at 但没有任何地方清理。

任务：
1. 新增迁移 V009：为 client_heartbeats 和 audit_logs 各建 DEFAULT 分区兜底。
   脚本必须可重复执行（IF NOT EXISTS）。
2. 新增分区维护逻辑（可并入 RetentionCleanupWorker，也可单独 hosted service）：
   每天执行一次，保证「当前月 + 未来 3 个月」的月度分区存在。
   必须复用现有 ScheduledLockService 防止并发重复执行。
   注意：存在 DEFAULT 分区时 ATTACH 新分区会扫描 DEFAULT 分区并加锁，所以要提前建、不要等到当月。
3. /health/db 增加只读检查项：未来 30 天分区是否齐备，缺失则返回 Degraded。
4. 遥测清理（保留天数写入 system_settings，给默认值）：
   - client_service_states：默认保留 7 天，分批删除（每批 5000 行），避免长事务
   - client_heartbeats：按分区 DROP 而不是 DELETE，默认保留 30 天
   - agent_notifications：删除 expires_at < now() 的行
5. 补测试：分区缺失时写入失败、维护任务执行后写入成功、清理任务按保留期删除且幂等。

不要做：这一批不要重构 client_service_states 的表结构（「状态变化才写一行」放到批次 5）。

验收：
- 把插入时间设为 2027-02，心跳与审计写入成功
- 手工删掉未来分区后 /health/db 变为 Degraded
- 清理任务在 10 万行遥测数据上执行不产生长时间锁表
```

---

## 批次 3 · 安全边界收敛（#4、#6、#7、#8、#9）

```
读 docs/REVIEW-2026-08-13.md 的 #4、#6、#7、#8、#9。五项都是小改动，一批做掉。

#4 ServerInstaller 把同一个口令同时用作 postgres 超级用户和 backup_monitor_app 应用角色。
    拆成两个独立随机口令；连接串只用应用角色口令；超级用户口令仅安装/迁移期使用，不进运行时配置。

#6 LocalServerBootstrap 把 DeploymentMode、LanMode:AutomaticEnrollment、LanMode:PrivateNetworkOnly
    硬写死在最后加载的内存配置源里，环境变量都覆盖不掉，导致免令牌自动登记永远关不掉。
    - 这三个键改为与机密同样的优先级：显式配置/环境变量 > 持久化 > 默认
    - 增加登记窗口 LanMode:EnrollmentOpenUntil（默认安装后 24 小时），窗口外免令牌登记一律 401
    - 管理网页增加「开放登记 30 分钟」按钮（需 perm:clients.manage）
    - 自动登记成功时写一条 Info 级告警，并在「待办」页显示最近自动登记的客户端
    - Security:AllowLegacyCommandHmac=false 和 AllowDevelopmentHeader=false 保持强制写死，
      但补注释说明为何与其它键处理方式不同

#7 Agent 私钥用 ProtectedData.Protect(bytes, null, LocalMachine) 保护，无 entropy，
    且 %ProgramData%\BackupMonitor\Agent 未收紧 ACL —— 本机任意标准用户都能解出客户端私钥和证书。
    - 安装器对 Agent 数据目录应用 ACL：断开继承，只留 SYSTEM 和 Administrators
    - Protect/Unprotect 传入固定的应用 entropy
    （更彻底的 CNG 机器密钥容器方案先不做，在报告中记录为后续项）

#8 密钥文件先写 .tmp（继承目录 ACL）再 Move，之后才收紧权限；数据目录、Repository、Staging
    完全没有 ACL 处理，备份数据对本机 Users 可读。
    - 提取共享的 SecureDirectory/SecureFile 工具，创建时即指定 DirectorySecurity
    - ApplySecretAcl 改为先断开继承并清除全部现有显式规则，再添加两条允许规则
    - 维护模式增加「权限自检并修复」动作

#9 两个启动期陷阱：
    (a) server-secrets.json 丢失但 client-ca.pfx 还在时，会生成新随机口令并跳过 CA 生成，
        导致运行时 CA 加载失败、全部 Agent mTLS 报废。要在启动时校验配对关系，
        失败则抛出带明确处置指引的异常，绝不静默继续。
        维护模式增加「导出/导入服务端密钥包」（secrets + CA 一起）。
    (b) AcquireCrossProcessLock 用 FileMode.CreateNew，进程崩溃后锁文件残留会让服务永久起不来。
        改为 FileMode.OpenOrCreate + FileShare.None，或改用命名 Mutex；
        超时异常消息必须包含锁文件路径和处置方式。

验收：
- 用应用角色口令登录 postgres 超级用户必须失败
- LanMode__AutomaticEnrollment=false 重启后免令牌注册返回 401；窗口过期后同样 401
- 标准用户读取 Agent state.json 被拒绝
- 删除 secrets 保留 CA 后启动，给出明确错误而不是生成新 CA
- 强杀服务端进程后再次启动正常
```

---

## 批次 4 · 稳定性与构建覆盖（#10、#11、#12、#13、#19）

```
读 docs/REVIEW-2026-08-13.md 的 #10、#11、#12、#13、#19。

#10 LanDiscoveryHostedService 的 new UdpClient(...) 在 try 之外，端口 45808 被占用时抛
     SocketException，而 .NET 8 默认 StopHost —— 一个可选功能能杀掉整个备份服务端。
     把绑定放进 try，失败只记 LogError 并退出该后台服务；RespondAsync 不要阻塞接收循环；
     顺带对发现响应加简单限流（每来源 IP 每秒 N 次）。

#11 V005 有两份实现：MigrationRunner 用 C# 重写（因为 SQL 里有 psql 变量 :'admin_pw'），
     但记录的 checksum 是 SQL 文件的哈希，改了 SQL 不改 C# 永远发现不了。
     测试夹具是第三种执行方式（字符串替换），导致生产迁移路径零测试覆盖。
     - V005 改用统一占位符（如 ${ADMIN_PASSWORD}），MigrationRunner 替换为 Npgsql 参数
       （参数化，不是字符串拼接）后整段执行，删除 ApplyV005Async
     - 测试夹具改为调用 MigrationRunner，让测试跑的就是生产代码
     - 迁移执行前加 pg_advisory_lock 防止并发迁移

#12 已安装时按钮变成「修复/升级」但仍走 InstallAsync 且强制要求输入管理员密码，
     而 V005 已应用会被跳过 —— 输入的密码什么都没做，界面却提示成功。
     升级模式隐藏密码框，AdminPassword 改为可空、仅首次安装必填；
     若用户仍填了密码，明确引导到「重置管理员密码」。

#13 LanNetworkPolicy.IsPrivateOrLoopback 不处理 IPv4-mapped IPv6（::ffff:192.168.1.5 会被判为非私网）。
     开头加一行 MapToIPv4 归一，与 ClientCertificateAuthenticationHandler 的做法保持一致；
     补测试覆盖 ::ffff:10.0.0.1、::ffff:127.0.0.1、::1、fe80::1、8.8.8.8。

#19 自动化构建只编译三个项目，两个安装器项目从未被验证过；测试用 postgres:15 而交付内置 16.4。
     - 构建改为 dotnet build src/BackupMonitor.sln -c Release，一次覆盖 9 个项目
     - 测试镜像改为 postgres:16
     - 新增 MigrationRunnerTests：完整跑 V001~V00N、重复执行幂等、篡改文件后拒绝继续

验收：
- 占用 UDP 45808 后启动服务端，API 正常提供服务，日志有明确告警
- dotnet build 解决方案 0 错误；dotnet test 全绿
- 升级安装不再要求也不再声称修改管理员密码
```

---

## 批次 5 · 性能与协议健壮性（#14、#15、#16、#17、#18）

```
读 docs/REVIEW-2026-08-13.md 的 #14、#15、#16、#17、#18。

#14 心跳写放大：每次心跳全删全插 client_disks 和 client_user_sessions，每个服务插一行状态，
     并且无论有没有告警都对 CPU/内存/每块源盘各执行一次 Raise 或 Recover。
     - 磁盘与会话改为按业务键 upsert + 删除本次未上报的行
     - client_service_states 改为「状态发生变化才写一行」，当前状态放快照列
     - RecoverAsync 先查是否存在活动告警，无则跳过
     - 对 Disks/ServiceStates/UserSessions 增加条数上限校验（64/128/128），超限截断并记警告

#15 AgentApiClient 每次构造请求都调 AgentStateStore.Snapshot()，而 Snapshot 是「整份状态序列化再
     反序列化」，状态里装着全量候选文件的路径和 SHA-256。上传 1 万文件时每个分块请求都要序列化数 MB JSON。
     - 为 ClientId/MachineId/ConfigVersion 提供直接属性访问（加锁但不拷贝）
     - SeenNotificationIds 和 SeenCommandNonces 做环形裁剪（最近 500 条或 7 天）

#16 心跳固定返回最近 24 小时内最多 20 条托盘消息，服务端不记投递状态，24 小时内每次心跳重复下发。
     agent_notifications 增加 delivered_at，领取时批量置为已投递，查询条件改为未投递。

#17 签名规范化脆弱：CommandPayload 用 expiresAt.ToString("O")，DateTimeKind 不一致就静默验签失败；
     ConfigPayload 直接序列化 DTO 作为签名原文，DTO 一改新旧端互不认账。
     - 时间改用 Unix 毫秒时间戳参与签名
     - 配置签名改为对显式拼接的字段序列做规范化（固定顺序、固定分隔符、明确空值表示）
     - 签名串加版本前缀 v1|，保留对前一版本的兼容窗口以支持 Agent 灰度升级
     - 补跨端签名/验签一致性测试，直接断言字节序列

#18 客户端证书 365 天有效期，没有任何续签路径，一年后全部客户端集体失效。
     - 新增 POST /api/v1/agent/certificate/renew（mTLS 认证），剩余有效期 < 30 天时 Agent 主动续签
     - 旧证书在新证书生效后置为 Superseded，保留 7 天重叠期
     - 客户端列表增加「证书剩余天数」列，30 天内到期产生告警
     - 心跳响应增加 certificateRenewalRequired 提示位

验收：
- 50 客户端模拟心跳下，单次心跳的数据库写入语句数明显下降
- 1 万文件备份集上传，Agent CPU 占用与耗时明显下降
- 同一条托盘消息只下发一次
- 新旧版本签名在兼容窗口内互通
- 证书剩余 29 天时 Agent 自动完成续签，续签期间不中断心跳
```

---

## 批次 6 · 前端与文档收尾（#20、#21、#22）

```
读 docs/REVIEW-2026-08-13.md 的 #20、#21、#22。

#20 build-turnkey.ps1 直接复制构建机上已安装的 PostgreSQL（默认 C:\Program Files\PostgreSQL\16），
     payload-manifest.json 只是事后记录哈希而不是事前校验。
     改为：从官方地址下载指定版本 zip → 比对写死的预期 SHA-256 → 解压使用；已下载则复用本地缓存；
     校验失败立即中止。许可证/版权文件复制逻辑保留，并记录来源 URL。

#21 前端三个问题，按顺序做：
     (a) 大量内联 onclick="App.act(...)" 使 CSP 的 script-src 'self' 无法启用。
         改为事件委托 + data-action / data-id 属性，统一在容器上监听。
         改完后把批次 1 加的 CSP 收紧到包含 script-src 'self'。
     (b) 全面审一遍 67 处 innerHTML，所有非受控值一律走 esc()。
         例如 views/backups.js 的「没有匹配「${st.keyword}」的备份集」当前是裸插值。
     (c) 令牌存储：access token 改为只存内存变量，refresh token 改为
         HttpOnly; Secure; SameSite=Strict Cookie（需后端配合）。依赖批次 1 的 HTTPS 已落地。

#22 文档：
     - docs/OPEN-ISSUES-2026-08-07.md 的 11 项已全部关闭，改名归档为 OPEN-ISSUES-2026-08-07.md 保留历史
     - AgentBootstrapController 的类注释仍写「注册令牌仍由管理员在安装器中输入」，与 LanSimple 免令牌矛盾
     - AgentHeartbeatService 的「分区缺失时由数据库维护任务兜底」注释在批次 2 后应指向真实存在的任务
     - docs/LAN-TURNKEY-ACCEPTANCE.md 增加回链，说明 #1 正是「E2E 未闭环」所掩盖的问题

不要做：不要引入任何构建工具、CSS 框架或前端库 —— 零构建是刻意的设计约束。
不要改信息架构和视觉设计。

验收：
- 在没有安装 PostgreSQL 的机器上能完整打包
- 浏览器控制台无 CSP 违规，全部交互功能不回归
- 页面在明暗两套主题下功能一致
```
