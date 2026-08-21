# 2026-08-13 审查问题闭环记录

日期：2026-08-13（Asia/Tokyo）  
仓库：`E:\GitHub\systemdataup`  
依据：[`REVIEW-2026-08-13.md`](./REVIEW-2026-08-13.md)、[`REVIEW-2026-08-13-PROMPTS.md`](./REVIEW-2026-08-13-PROMPTS.md)、[`DEV-PROMPTS.md`](./DEV-PROMPTS.md)

## 结论

审查报告中的 #1～#22 已按批次完成代码、迁移、测试、供应链脚本或文档闭环。这里的“已解决”表示仓库内实现和可执行门禁已经补齐；干净 Windows Server/Windows 11 虚拟机上的双击安装、重启、卸载、跨机 LAN/mTLS 业务链路，以及浏览器视觉/交互验收，仍属于需要外部验收环境的独立门禁，不能用本地构建或 `node --check` 冒充。

## 22 项闭环

| 编号 | 状态 | 本次闭环与证据 |
|---:|:---:|---|
| #1 | ✅ | Turnkey HTTPS、客户端证书认证、服务端证书指纹固定和安全临时目录已落地；最小真实探针在 `X-Client-Id` 且无客户端证书时得到 HTTP 401。相关代码：`ServerCertificateFactory`、`LocalServerBootstrap`、`AgentApiClient`、`SecurityHeadersMiddleware`。 |
| #2 | ✅ | `V009__partition_defaults_and_retention.sql` 增加分区默认兜底；`PartitionMaintenanceWorker` 负责提前维护分区，避免日期跨越后心跳/审计事务回滚。 |
| #3 | ✅ | `client_service_states` 改为状态变化写历史、当前快照单行维护；心跳明细和通知清理纳入 `RetentionCleanupWorker`，并保留实际 PostgreSQL 清理测试。 |
| #4 | ✅ | 服务端安装和迁移流程拆分超级用户口令与应用角色口令，运行时连接只使用应用角色。 |
| #5 | ✅ | 安全响应头加入 CSP、`nosniff`、`DENY`、`no-referrer`；Turnkey HTTPS 已与 #1 同批闭环。 |
| #6 | ✅ | `DeploymentMode`、LAN 自动登记开关和登记窗口改为显式配置优先于持久化配置；LanSimple 的免令牌路径与 Secure 的令牌路径分开。 |
| #7 | ✅ | Agent 状态、客户端证书和私钥使用固定 entropy 的 DPAPI 保护；Agent 数据目录和配置文件应用 Windows ACL。 |
| #8 | ✅ | 密钥和敏感文件先在受保护的临时文件中写入，再原子移动；SecureFileSystem 统一收紧 secrets、repository、staging 等路径的权限。 |
| #9 | ✅ | 启动时校验服务端 secrets 与 CA/证书配对关系；跨进程锁支持崩溃后恢复，维护入口提供导出/导入/修复路径。 |
| #10 | ✅ | UDP 发现端口冲突不再拖垮 API；监听启动失败只记录错误并退出后台服务，响应发送不阻塞接收循环，并加入来源限流。 |
| #11 | ✅ | `MigrationRunner` 以原始 SQL 计算 checksum，执行时只剥离顶层事务包装；V005 使用 `${ADMIN_PASSWORD}` 参数占位符，测试直接调用生产迁移执行路径。 |
| #12 | ✅ | 安装模式与升级/修复模式分开；升级不再强制要求或静默忽略管理员口令，已存在配置时按升级语义处理。 |
| #13 | ✅ | `LanNetworkPolicy` 先归一 IPv4-mapped IPv6，再执行私网/回环判断，并补齐对应测试。 |
| #14 | ✅ | 心跳磁盘、会话按业务键 upsert 并删除本次未上报项；服务状态只在变化时写历史，明细上限为 64/128/128，超限截断并告警。 |
| #15 | ✅ | Agent 请求不再深拷贝整份状态；`AgentStateStore` 提供加锁的直接属性，通知/nonce 集合按上限裁剪。 |
| #16 | ✅ | `agent_notifications.delivered_at` 和领取事务落地；使用事务、行锁和 `FOR UPDATE SKIP LOCKED` 领取，重复心跳不会重复领取同一条通知。 |
| #17 | ✅ | `AgentSignatureCanonicalizer` 固定 v1 前缀、字段顺序、Base64Url、Unix 毫秒时间和配置排序；验证器保留旧格式兼容窗口，并有跨端协议测试。 |
| #18 | ✅ | 增加 Agent 证书续签 API；客户端在剩余不足 30 天时主动续签，旧证书在新证书生效后保留 7 天重叠期，列表展示剩余天数并产生到期告警。 |
| #19 | ✅ | 构建入口覆盖 `src/BackupMonitor.sln` 的 9 个项目；迁移执行、重复执行和版本差异测试已加入，测试镜像目标改为 PostgreSQL 16。 |
| #20 | ✅ | `scripts/payload-lock.json` 锁定 PostgreSQL `16.14-1` 官方 Windows binary zip、URL 和 SHA-256；`build-turnkey.ps1` 只接受校验通过的缓存/下载归档，校验失败立即停止，并复制 license/copyright 文件、记录 `source_url` 和归档哈希。 |
| #21 | ✅ | 前端生成的动作改为 `data-ui-action` + 容器事件委托；源目录不再有 HTML `onclick` 属性或 `.onclick` 文本，CSP 收紧为 `script-src 'self'`。所有已审计外部值走 `esc()` 或安全格式化函数，含 backups/clients 空状态搜索词；access token 仅存内存，refresh token 由后端 `__Host-backupmonitor-refresh` HttpOnly、Secure、SameSite=Strict Cookie 管理。 |
| #22 | ✅ | 已关闭问题单原样归档为 [`OPEN-ISSUES-2026-08-07.md`](./OPEN-ISSUES-2026-08-07.md)；修正 `AgentBootstrapController` 对 Secure/LanSimple 令牌语义的注释，修正 `AgentHeartbeatService` 指向真实维护 Worker 的注释；[`LAN-TURNKEY-ACCEPTANCE.md`](./LAN-TURNKEY-ACCEPTANCE.md) 已回链说明 #1 曾被 E2E 未闭环掩盖。 |

## 最终门禁证据

| 门禁 | 结果 |
|---|---|
| `dotnet build src/BackupMonitor.sln -c Release --nologo` | 通过：9 个项目，0 警告，0 错误。 |
| 前端 `wwwroot/js` 全量 `node --check` | 通过：19/19。 |
| 前端静态安全断言 | 通过：无 HTML 内联事件属性、无 `.onclick` 文本、无 `store` 读写 access/refresh token、无残留 `?{` 模板插值；`index.html` 无内联脚本，CSP 含 `script-src 'self'`，并显式保留现有 `style=` 模板所需的 `style-src 'self' 'unsafe-inline'`。 |
| PostgreSQL payload 锁 | 通过：缓存归档实际 SHA-256 `98af1417ba6a8dc30543e560e5407833a3b9e7cc7ed20e73b2006f3aa2f04663` 与锁文件一致；PowerShell 解析通过。 |
| 本地 PostgreSQL 集成测试 | 本次最终门禁使用真实本地 PostgreSQL 临时实例验证：86/86 通过、0 失败、0 跳过；协议/通知聚焦回归 4/4 通过。 |
| Docker/Testcontainers | 未通过环境门禁：本机 Docker daemon 的 `npipe://./pipe/docker_engine` 不可用；没有把该环境阻塞伪装成测试通过。 |
| 干净 VM 与浏览器 UAT | 未执行，需外部提供可反复安装、重启、卸载的 Windows Server/Windows 11 VM 以及浏览器交互验收会话。 |

## 交付边界

本次已重新执行 `scripts/build-turnkey.ps1 -Configuration Release -Runtime win-x64`，重新生成 `dist` 下的服务端/客户端单文件安装器、服务端 payload、payload manifest 和 SHA-256 清单。当前服务端安装器为 398,069,144 bytes（SHA-256 `7ab23ce2b29b0d8531063f2f1dfd08d5bd4d711e259ac7b25b581d4060592e63`），客户端安装器为 161,925,626 bytes（SHA-256 `d52b9dacbbfb2b7e4727b16f1fb713b2826bfae406e7f9f194bbb94acb9d04ff`）；payload 内容核验包含 API、V001-V010 数据库迁移和 PostgreSQL 16.14。此次已完成发布物生成、SHA-256、内嵌 manifest、EXE 图标和 payload 静态内容核验；干净 VM 双击安装、重启、卸载以及跨机 LAN/mTLS 业务链路仍需外部环境验收。

本次未执行 git commit 或 push。

## 2026-08-13 新缺陷批次（P0/P1/P2）

本批次针对 closure 后发现的三个新缺陷完成修复，并重新生成了 Turnkey 发布物。

### 修复内容

| 缺陷 | 修复结果 |
|---|---|
| 甲：Turnkey Kestrel 无法取得服务端 PFX 口令 | `Program.cs` 在本地 Turnkey 引导完成后，从 `Security:ServerCertificate:CertPath/Password` 显式加载证书，并通过 `ListenAnyIP(...).UseHttps(certificate)` 注册唯一 HTTPS 端点；`LoadForKestrel` 使用 `PersistKeySet`，不使用 `EphemeralKeySet`。原来与实现不符的注释已改为说明 Secure/Turnkey 的真实监听行为。 |
| 乙：5080 HTTP/HTTPS 双重监听 | Secure 的 `appsettings.Production.json` 不再声明 Kestrel HTTP 端点；安装器生成的 Turnkey 配置显式写入 `Kestrel:Endpoints:Http = null`，程序同时清空 Kestrel 配置加载器，避免升级时残留旧 HTTP 键被合并。`ServerInstaller.Validate` 增加 HTTP 与 HTTPS 端口不得相同及端口范围校验。 |
| 丙：`dbinit.bat` 与 V005/迁移范围漂移 | `MigrationRunner` 移到 Infrastructure 作为安装器与 `dbinit.bat` 共用的唯一实现；API 增加非安装器 `--migrate` 入口，批处理调用该入口并覆盖 V001-V010。V005 继续以参数化 `${ADMIN_PASSWORD}` 绑定 `ADMIN_PW`，不再使用过期的 `-v admin_pw`。 |
| P2：服务端证书指纹与重复 PFX 加载 | 服务端 bootstrap、Server.Setup 和 Agent pinning 统一使用 SHA-256；`/api/v1/agent/bootstrap` 改为读取启动时缓存的指纹和 PEM，不再按请求从磁盘加载 PFX。 |

### 本批次验收证据

| 门禁 | 结果 |
|---|---|
| Turnkey 启动与监听 | `READY=True`、进程保持运行；日志出现 `Turnkey HTTPS endpoint bound by Kestrel ... port 5080`；`LISTENER_COUNT=1`。即使人为残留旧 `appsettings.Production:Kestrel:Endpoints:Http`，兼容性探针仍只有一个 HTTPS socket。 |
| HTTPS health | `curl --insecure https://127.0.0.1:5080/health` 返回 HTTP 200，body 为 `{"status":"healthy",...}`。 |
| 证书 pinning | 错误 SHA-256 指纹连接失败（`PIN_WRONG=FAILED_AS_EXPECTED`）；正确指纹连接返回 200（`PIN_CORRECT=200`）。 |
| 客户端认证边界 | 不带客户端证书、仅带 `X-Client-Id` 请求 Agent config 返回 HTTP 401。 |
| 全新 `dbinit.bat` | `DBINIT_EXIT=0`；admin 为 `active`；`schema_migrations` 包含 V001、V002、V003、V004、V005、V006、V007、V008、V009、V010。 |
| 构建与测试 | `dotnet build src/BackupMonitor.sln -c Release --nologo`：9 项目、0 警告、0 错误；`dotnet test`：86/86 通过、0 跳过。 |

本地 Turnkey HTTP/HTTPS、证书 pinning 和认证探针在临时 PostgreSQL 实例上完成；由于当前自动化终端是 UAC 中等完整性令牌，探针副本临时关闭了目录 ACL 强制以验证网络链路，源码已恢复 `LocalServerBootstrap` 默认 ACL 强制，最终构建与发布物均来自恢复后的源码。Windows 服务实际由安装器注册为 LocalSystem；干净 VM 上的安装/重启/卸载仍按交付边界进行外部验收。

### 本批次当时重新生成的发布物（已被后续复测包取代）

- `dist/BackupMonitor.Server.Setup.exe`：397,954,456 bytes，SHA-256 `98d0d09eaba04e38b83a3baf2d635dec2e07674cfff867eb4540eb4f7b8fe0ca`
- `dist/BackupMonitor.Agent.Setup.exe`：161,880,570 bytes，SHA-256 `e601101b9bc63a63daf7423b3e102453d72277c7d3cc305877692d8456f83436`
- `dist/payload-manifest.json`：已同步生成；PostgreSQL payload lock/static hash 校验沿用并通过。

## 2026-08-13 安装包复测修复：initdb 口令文件权限

用户实机首次运行服务端安装包时，安装器在“正在初始化 BackupMonitor 专用 PostgreSQL”阶段失败，错误为：

```text
initdb.exe exited with code 1: could not open file
C:\Users\Administrator\AppData\Local\Temp\BackupMonitor-pg-*.txt: Permission denied
```

根因是安装器为了保护临时 `--pwfile`，只写入 SYSTEM 和 Administrators 两个 ACL；在 UAC 场景下，`initdb.exe` 子进程使用的管理员令牌不一定能通过 Administrators 组 ACE 读取该文件。

修复内容：

- `SecureFileSystem.ApplyFileAcl` 增加可选的当前用户显式 ACE；默认行为不变，其他密钥和数据文件仍只授予 SYSTEM + Administrators。
- `PostgreSqlManager` 仅对短生命周期的 `initdb --pwfile` 临时文件启用当前安装用户 ACE，同时保留 SYSTEM + Administrators 保护和失败清理逻辑。
- 重新构建并重新生成服务端/客户端安装包。

复测门禁（本节产物已被后续复测包取代）：

- `dotnet build src/BackupMonitor.sln -c Release --nologo`：9 项目、0 警告、0 错误。
- `dotnet test ...BackupMonitor.Infrastructure.Tests.csproj -c Release --nologo`：86/86 通过、0 失败、0 跳过。
- 新服务端安装包：397,954,456 bytes，SHA-256 `98d0d09eaba04e38b83a3baf2d635dec2e07674cfff867eb4540eb4f7b8fe0ca`。
- 新客户端安装包：161,880,570 bytes，SHA-256 `e601101b9bc63a63daf7423b3e102453d72277c7d3cc305877692d8456f83436`。

本节 SHA-256 为 `98d0d09e...` / `e601101b...` 的安装包已被下一节产物取代，不应继续使用。

## 2026-08-13 安装包复测第二轮：迁移残留权限、界面遮挡与 Logo

用户在应用 initdb 口令文件修复包后，安装器继续执行到迁移阶段，但报错：

```text
Access to the path
'C:\ProgramData\BackupMonitor\Server\database\V001__initial_schema.sql' is denied.
```

### 深度根因

- `C:\ProgramData\BackupMonitor\Server\database` 是旧安装器复制迁移 SQL 的运行时数据路径；失败安装会把该目录作为残留留在磁盘上，重试时旧实现再次覆盖或读取同一路径，使安装流程受旧文件 ACL、占用状态和历史安装残留影响。
- 迁移 SQL 实际是不可变安装资源，不属于服务端运行数据，更不应与 secrets、repository、staging 等需收紧 ACL 的运行时目录混放。
- 安装器本来已经把 `server-payload.zip` 解压到本次运行独享的私有临时目录，却又将其中的 `database` 复制到 ProgramData 后再执行，制造了无必要的第二份可变副本和重试冲突。
- 界面使用固定尺寸窗口和一个 `SizeType.Percent, 100` 的空白/发现行，其余内容依赖 `AutoSize`；系统字体或 DPI 放大时，中部状态、进度条及底部按钮会被挤压或裁切。两个安装项目还显式设置了 `ShowIcon = false`，且未配置 `ApplicationIcon`，所以 EXE 和标题栏不可能显示项目 Logo。

### 修复内容

- `ServerInstaller` 不再把 `payloadRoot\database` 复制到 `request.DataDirectory\database`；`MigrationRunner` 直接读取本次安装私有解压目录中的 `payloadRoot\database`。旧失败安装留下的 `ProgramData\...\database` 即使存在、只读或权限异常，也不再参与新安装/修复流程。
- `SecureFileSystem` 为受保护目录写入 SYSTEM 和 Administrators 的可继承 FullControl ACE，保证之后创建的子目录和文件继承预期权限；单文件 ACL 与 initdb 临时口令文件的当前用户例外策略保持不变。
- Server/Agent 安装窗体统一改为 `PerMonitorV2`、`AutoScaleMode.Dpi`、可调整大小和可滚动布局；所有表格行使用内容自适应，移除会吞占中部空间的百分比行，状态文本使用固定高度与省略保护，避免覆盖进度条或底部按钮。
- 服务端 payload 的大 ZIP 解压和 API 目录递归复制改为在线程池执行；WinForms UI 线程不再被长时间同步文件 I/O 占用，窗口可以持续重绘并响应操作，避免安装期间出现“（未响应）”。
- 复用网页已有的 BackupMonitor 盾牌标志，生成包含 16/24/32/48/64/128/256 七种尺寸的 `src/assets/backupmonitor.ico`；两个安装项目均配置 `ApplicationIcon` 并恢复标题栏图标显示。
- Agent 安装器首次默认地址和提示统一为 `https://127.0.0.1:5080` / `https://192.168.1.20:5080`，不再默认连接 Turnkey 已禁用的 HTTP 端点。

### 本轮门禁与产物证据

| 门禁 | 结果 |
|---|---|
| Release 构建 | `dotnet build src/BackupMonitor.sln -c Release --nologo`：9 项目、0 警告、0 错误。 |
| 完整测试 | 默认 Testcontainers 路径因本机 Docker `npipe://./pipe/docker_engine` 不可用而无法初始化数据库夹具；按既有基线使用本机 PostgreSQL 16 临时实例复跑后 86/86 通过、0 失败、0 跳过。 |
| 安装界面视觉核对 | 以 DLL 入口打开编译后的 Server/Agent 窗体，仅查看不触发安装；标题、输入区、发现区、状态、进度条、主按钮和维护按钮均完整显示，两个标题栏均显示盾牌 Logo。 |
| EXE 图标 | 两个最终 EXE 均可提取 32×32 应用图标，提取后的 PNG SHA-256 均为 `a33940c33efbbb7660801baa0b85f46294f59a0d72071bf1f2650ed26975d750`；源 ICO 包含七种尺寸。 |
| EXE manifest | 使用 Windows SDK `mt.exe` 从两个最终 EXE 提取并核对：均包含 `requestedExecutionLevel level="requireAdministrator"` 和 Windows 10/11 compatibility GUID。 |
| 服务端 payload | `server-payload.zip` 含 V001、V002、V003、V004、V005、V006、V007、V008、V009、V010，共 10 个迁移；安装执行路径只引用 `payloadRoot\database`，不再引用 `request.DataDirectory\database`。 |
| 当时的新服务端安装包（已被第三轮取代） | 398,065,048 bytes；SHA-256 `a0ed49373f577162076986676b4c7cd1817175452256a353849c1d71d42506c5`。 |
| 当时的新客户端安装包（已被第三轮取代） | 161,925,626 bytes；SHA-256 `7436f8f2c967c0d27c1368a4ac40641fd5ded0c3b6da466c3581cf60f5002726`。 |

本节 `a0ed4937...` / `7436f8f2...` 产物已被下一节第三轮修复包取代。旧 `C:\ProgramData\BackupMonitor\Server\database` 残留不再影响新版安装器，用户无需手工修改该目录 ACL 或删除其中 SQL 文件。

## 2026-08-13 安装包复测第三轮：PostgreSQL 数据目录权限与服务持久化

用户再次运行上一轮安装包时，安装器仍停在 PostgreSQL 初始化阶段，错误为：

```text
initdb: 错误: 无法访问目录
"C:/ProgramData/BackupMonitor/PostgreSQL/data": Permission denied
```

### 深度根因

- 安装器对 PostgreSQL `data` 目录只保留 SYSTEM 和 Administrators ACL。Windows 版 PostgreSQL 会以受限子进程执行初始化/服务工作；仅依赖 Administrators 组 ACE 不足以保证该进程能进入数据目录，必须给本次安装用户的具体 SID 授予可继承权限，并继续保留 SYSTEM 供 LocalSystem 服务使用。
- 旧代码先收紧目录 ACL，再用 `File.Exists(data\PG_VERSION)` 判断数据簇是否初始化。`.NET File.Exists` 会把拒绝访问折叠为 `false`，因此权限异常被错误解释成“没有 PG_VERSION”，安装器随即对一个已经完整初始化的数据簇再次执行 `initdb`。
- 现场只读核对显示 `data\PG_VERSION` 为 `16`，目录含完整 PostgreSQL 数据簇，55432 端口已有 PostgreSQL 监听，但 `BackupMonitor.PostgreSQL` Windows 服务不存在；日志还显示 `backup_monitor_app` 角色尚未创建。这说明旧安装停在“数据簇启动后、应用角色/迁移前”，不是一个应被清空或重建的数据目录。
- 旧连接探针把“服务未运行”和“口令认证失败”混为一类；对已有数据簇若密钥不匹配，可能错误进入重复启动逻辑。认证失败现在被单独识别并明确终止，安装器绝不重新执行 `initdb` 或覆盖已有数据。
- PostgreSQL 运行时原来直接从安装器的临时 payload 目录运行并注册服务，而该临时目录在安装结束或失败后会被删除；即使首次安装看似成功，服务注册也会指向不可持久化的可执行文件，重启后必然失效。
- 首次启动 PostgreSQL 后，旧流程只注册服务，没有把手工启动的进程切换为真正由 Windows 服务托管的进程；且若服务注册后后续步骤失败，异常清理可能遗漏这个半成品服务。

### 修复内容

- `SecureFileSystem.CreateDirectory/ApplyDirectoryAcl` 增加可选的当前用户 SID；仅 PostgreSQL 数据目录和短生命周期 `initdb --pwfile` 启用该例外，并授予目录/文件可继承 FullControl。其他 secrets、证书和运行数据的默认 SYSTEM + Administrators 策略不变。
- `PostgreSqlManager` 改为用显式打开并读取 `PG_VERSION` 判定数据簇。只有文件确实不存在才视为未初始化；拒绝访问、空版本文件等均给出保护性错误，不再降级成 `initdb`。
- 没有 `PG_VERSION` 但目录非空时，不删除内容，而是原子移动到同级 `data.incomplete-时间戳-guid` 备份后再初始化，避免历史半成品污染重试并保留恢复材料。
- PostgreSQL 存活判断改用 `pg_ctl status`；已有数据簇的超级用户认证失败被单独报告为密钥不匹配，禁止重建、重复启动或覆盖数据。
- PostgreSQL 完整运行时现在复制到持久目录 `<服务端安装目录>\PostgreSQL`。安装/修复会校验 `initdb.exe`、`pg_ctl.exe`、`postgres.exe`、`lib` 和 `share`；有效的现有运行时在修复时直接复用，避免覆盖被 Windows 锁定的程序文件。
- 安装器检查现有 `BackupMonitor.PostgreSQL` 服务的 `ImagePath`。若仍指向旧临时位置，则先停止并注销旧服务，再用持久 `pg_ctl.exe` 自动注册；若发现没有服务托管的历史孤立进程，则先 `pg_ctl stop -m fast -w`，随后启动真正的 Windows 服务，再继续创建角色、数据库和执行迁移。
- 安装失败清理改为检查 PostgreSQL 服务的实际存在状态，确保“服务已注册但后续步骤失败”的首次安装不会遗留半配置服务。

### 本轮门禁与最终产物

| 门禁 | 结果 |
|---|---|
| 真实 `initdb` ACL 探针 | 使用发布 payload 中的 `initdb.exe`，在临时数据目录应用 SYSTEM、Administrators、当前用户 SID 三项可继承 FullControl 后执行；`INITDB_EXIT=0`、`PG_VERSION_EXISTS=True`，随后完整清理探针目录。未修改现场 ProgramData 数据。 |
| 现场旧失败状态核对 | `PG_VERSION=16`；55432 只有一个监听；`BackupMonitor.PostgreSQL` 服务不存在。新版会复用数据簇、持久化运行时并把孤立进程切换为服务，不会再次执行 `initdb`。 |
| Release 构建 | `dotnet build src/BackupMonitor.sln -c Release --nologo`：9 个项目、0 警告、0 错误。 |
| 完整测试 | 使用真实本地 PostgreSQL 16 临时实例：86/86 通过、0 失败、0 跳过。 |
| payload | PostgreSQL `initdb.exe`、`pg_ctl.exe`、`postgres.exe`、`lib`、`share` 均存在；迁移为 V001-V010，共 10 个。 |
| EXE 图标与 manifest | Server/Agent 最终 EXE 均可提取 32×32 图标；源 ICO 含 7 个尺寸；两者均包含 `requireAdministrator` 和 Windows 10/11 compatibility GUID。 |
| 最终服务端安装包 | 398,069,144 bytes；SHA-256 `7ab23ce2b29b0d8531063f2f1dfd08d5bd4d711e259ac7b25b581d4060592e63`。 |
| 最终客户端安装包 | 161,925,626 bytes；SHA-256 `d52b9dacbbfb2b7e4727b16f1fb713b2826bfae406e7f9f194bbb94acb9d04ff`。 |

`dist/SHA256SUMS.txt` 已与实际文件重新计算并一致。此前所有服务端/客户端安装包（包括第二轮 `a0ed4937...` / `7436f8f2...`）均已过期，只能使用本节两个最终 SHA-256 对应的文件。现场数据簇没有被删除或重建；用户无需清理 `C:\ProgramData\BackupMonitor\PostgreSQL\data`，新版安装器会按修复/续装语义接管。
