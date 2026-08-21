# LAN Turnkey 批次 A：当前状态审计与契约冻结

审计日期：2026-08-11（Asia/Tokyo）  
仓库：`E:\GitHub\systemdataup`  
执行边界：仅执行 `docs/LAN-TURNKEY-CHANGE-PLAN.md` 的“批次 A：当前状态审计与契约冻结”；本次不实施批次 B～G。

## 0. 审计结论

本次审计的结论是：当前仓库可以完成 Release 编译，但不能宣称整套提示词 1～12 或 LAN Turnkey 交付目标已经完成。

- `C:\Program Files\dotnet\dotnet.exe build BackupMonitor.sln -c Release --nologo` 在 `src` 目录退出码为 `0`，8 个解决方案项目均成功编译，0 warning、0 error。
- 现有测试命令退出码为 `1`：59 个测试中 36 个通过、23 个失败、0 个跳过。23 个失败均在 `PostgresDatabaseFixture` 启动 Testcontainers PostgreSQL 时因为 Docker 引擎不可用而失败，不能计为通过。
- 当前客户端发布目录存在一个安装包和一个 Agent ZIP；已核对路径、大小、SHA-256。它们不是最终计划要求的 `dist` 双 EXE 交付目录。
- 当前没有 `src/server/BackupMonitor.Server.Setup` 项目，也没有 `dist/BackupMonitor.Server.Setup.exe`、`dist/BackupMonitor.Agent.Setup.exe` 或 `dist/SHA256SUMS.txt`。
- 现有 Agent、Tray、注册审批、心跳、通知和前端模块代码均能找到相应静态实现；但数据库集成、Windows 服务安装/恢复、浏览器视觉验收和完整 LAN Turnkey 链路在本次没有被验证为通过。
- 新 LAN Turnkey 目标要求的本地服务自举、持久化服务密钥、UDP 发现、免手工注册令牌、服务端安装/维护模式和最终双击交付仍属于后续批次 B～G，本次没有提前修改。

本报告写入前，工作区已有 `32` 个已跟踪修改项和 `103` 个未跟踪项；这些改动均保留。本次审计只新增本报告文件，没有覆盖、回滚或整理其他人员的改动。

## 1. 实际检查的文件

下表列出本次实际读取、检索或用于构建/测试/发布物核验的文件。目录项只表示对其下列出的文件进行了检查，不表示对目录中所有文件作了未经执行的推断。

### 1.1 规范、工作区和构建入口

- `docs/DEV-PROMPTS.md`（完整读取，505 行）
- `docs/LAN-TURNKEY-CHANGE-PLAN.md`（完整读取，279 行）
- `.gitignore`
- `.github/workflows/ci.yml`
- `src/BackupMonitor.sln`
- `src/src/BackupMonitor.Api/BackupMonitor.Api.csproj`
- `src/src/BackupMonitor.Core/BackupMonitor.Core.csproj`
- `src/src/BackupMonitor.Infrastructure/BackupMonitor.Infrastructure.csproj`
- `src/src/BackupMonitor.Shared/BackupMonitor.Shared.csproj`
- `src/tests/BackupMonitor.Infrastructure.Tests/BackupMonitor.Infrastructure.Tests.csproj`
- `src/agent/BackupMonitor.Agent/BackupMonitor.Agent.csproj`
- `src/agent/BackupMonitor.Agent.Tray/BackupMonitor.Agent.Tray.csproj`
- `src/agent/BackupMonitor.Agent.Setup/BackupMonitor.Agent.Setup.csproj`
- `dbinit.bat`
- `deploy/README.md`

解决方案实际包含 8 个项目：Api、Core、Infrastructure、Shared、Infrastructure.Tests、Agent、Agent.Tray、Agent.Setup。未发现服务端安装项目 `src/server/BackupMonitor.Server.Setup`。

### 1.2 数据库、认证和服务端契约

- `src/database/V001__initial_schema.sql`
- `src/database/V002__seed_data.sql`
- `src/database/V003__audit_logs.sql`
- `src/database/V004__refresh_tokens.sql`
- `src/database/V005__admin_password_bootstrap.sql`
- `src/database/V006__client_runtime_telemetry.sql`
- `src/database/V007__agent_notifications.sql`
- `src/src/BackupMonitor.Core/Enums/Enums.cs`
- `src/src/BackupMonitor.Core/Entities/Backup/BackupTask.cs`
- `src/src/BackupMonitor.Core/Entities/Client/ClientHeartbeat.cs`
- `src/src/BackupMonitor.Core/Entities/Client/AgentNotification.cs`
- `src/src/BackupMonitor.Core/Entities/Client/ClientUserSession.cs`
- `src/src/BackupMonitor.Infrastructure/Data/AppDbContext.cs`
- `src/src/BackupMonitor.Infrastructure/Data/Configurations/BackupConfigurations.cs`
- `src/src/BackupMonitor.Infrastructure/Data/Configurations/ClientConfigurations.cs`
- `src/src/BackupMonitor.Infrastructure/Data/Configurations/RbacConfigurations.cs`
- `src/src/BackupMonitor.Infrastructure/Services/AuthService.cs`
- `src/src/BackupMonitor.Api/Controllers/AuthController.cs`
- `src/src/BackupMonitor.Api/Controllers/AdminClientController.cs`
- `src/src/BackupMonitor.Api/Controllers/AdminReportController.cs`
- `src/src/BackupMonitor.Api/Controllers/AdminRegistrationTokenController.cs`
- `src/src/BackupMonitor.Api/Controllers/AgentBootstrapController.cs`
- `src/src/BackupMonitor.Infrastructure/Services/RegistrationTokenService.cs`
- `src/src/BackupMonitor.Infrastructure/Services/AgentRegistrationService.cs`
- `src/src/BackupMonitor.Infrastructure/Services/AgentHeartbeatService.cs`
- `src/src/BackupMonitor.Infrastructure/Services/AgentNotificationService.cs`
- `src/src/BackupMonitor.Infrastructure/Services/AlertingService.cs`
- `src/src/BackupMonitor.Infrastructure/Services/ReportService.cs`
- `src/src/BackupMonitor.Infrastructure/Services/UploadCommitWorker.cs`
- `src/src/BackupMonitor.Infrastructure/Services/CommandService.cs`
- `src/src/BackupMonitor.Infrastructure/Security/CommandSigner.cs`
- `src/src/BackupMonitor.Api/Program.cs`
- `src/src/BackupMonitor.Api/appsettings.json`
- `src/src/BackupMonitor.Api/appsettings.Development.json`
- `src/src/BackupMonitor.Api/appsettings.Production.json`

### 1.3 Agent、Tray、Setup 和发布脚本

- `src/agent/BackupMonitor.Agent/Program.cs`
- `src/agent/BackupMonitor.Agent/Options/AgentOptions.cs`
- `src/agent/BackupMonitor.Agent/Services/AgentWorker.cs`
- `src/agent/BackupMonitor.Agent/Services/AgentApiClient.cs`
- `src/agent/BackupMonitor.Agent/Services/AgentStateStore.cs`
- `src/agent/BackupMonitor.Agent/Services/BackupScanner.cs`
- `src/agent/BackupMonitor.Agent/Services/AgentNotificationStore.cs`
- `src/agent/BackupMonitor.Agent/Services/AgentNotificationService.cs`
- `src/agent/BackupMonitor.Agent/Security/CommandSignatureVerifier.cs`
- `src/agent/BackupMonitor.Agent/Installer/install-agent.ps1`
- `src/agent/BackupMonitor.Agent/Installer/uninstall-agent.ps1`
- `src/agent/BackupMonitor.Agent/Installer/README.md`
- `src/agent/BackupMonitor.Agent.Tray/Program.cs`
- `src/agent/BackupMonitor.Agent.Tray/TrayApplicationContext.cs`
- `src/agent/BackupMonitor.Agent.Setup/SetupForm.cs`
- `src/agent/BackupMonitor.Agent.Setup/AgentInstaller.cs`
- `src/agent/BackupMonitor.Agent.Setup/app.manifest`

### 1.4 测试和前端

- `src/tests/BackupMonitor.Infrastructure.Tests/PostgresDatabaseFixture.cs`
- `src/tests/BackupMonitor.Infrastructure.Tests/EnumDefaultValueRegressionTests.cs`
- `src/tests/BackupMonitor.Infrastructure.Tests/AuthSecurityRegressionTests.cs`
- `src/tests/BackupMonitor.Infrastructure.Tests/DatabaseHealthCheckTests.cs`
- `src/tests/BackupMonitor.Infrastructure.Tests/PathSafetyTests.cs`
- `src/src/BackupMonitor.Infrastructure/Health/DatabaseHealthCheck.cs`
- `src/src/BackupMonitor.Infrastructure/Security/PathSafety.cs`
- `src/src/BackupMonitor.Api/Controllers/DownloadsController.cs`
- `src/src/BackupMonitor.Api/wwwroot/index.html`
- `src/src/BackupMonitor.Api/wwwroot/css/tokens.css`
- `src/src/BackupMonitor.Api/wwwroot/css/base.css`
- `src/src/BackupMonitor.Api/wwwroot/css/components.css`
- `src/src/BackupMonitor.Api/wwwroot/js/app.js`
- `src/src/BackupMonitor.Api/wwwroot/js/dashboard.js`
- `src/src/BackupMonitor.Api/wwwroot/js/todo.js`
- `src/src/BackupMonitor.Api/wwwroot/js/notifications.js`
- `src/src/BackupMonitor.Api/wwwroot/js/client-runtime.js`
- `src/src/BackupMonitor.Api/wwwroot/assets/brand/mark.svg`
- `src/src/BackupMonitor.Api/wwwroot/assets/brand/wordmark.svg`
- `src/src/BackupMonitor.Api/wwwroot/assets/brand/favicon.svg`

`wwwroot/js` 下实际存在 19 个 JavaScript 模块；本次对这 19 个文件逐一执行了 `node --check`。

### 1.5 本次核对的发布物和发布目录

- `src/src/BackupMonitor.Api/wwwroot/downloads/BackupMonitor.Agent.Setup.exe`
- `src/src/BackupMonitor.Api/wwwroot/downloads/BackupMonitor.Agent.zip`
- `artifacts/BackupMonitor.Agent.Setup-publish/BackupMonitor.Agent.Setup.exe`
- `artifacts/BackupMonitor.Agent.zip`
- `artifacts/BackupMonitor.Agent-fixed.zip`
- `artifacts/BackupMonitor.Agent-package-20260809.zip`
- `artifacts/BackupMonitor.Agent-package-setup-20260810/`
- `src/src/BackupMonitor.Api/bin/Release/net8.0/BackupMonitor.Api.exe`
- `src/src/BackupMonitor.Api/bin/Release/net8.0/BackupMonitor.Api.dll`
- `src/agent/BackupMonitor.Agent/bin/Release/net8.0-windows/BackupMonitor.Agent.exe`
- `src/agent/BackupMonitor.Agent.Tray/bin/Release/net8.0-windows/BackupMonitor.Agent.Tray.exe`
- `src/agent/BackupMonitor.Agent.Setup/bin/Release/net8.0-windows/BackupMonitor.Agent.Setup.exe`

## 2. 构建和测试命令、退出码

命令均使用显式 .NET 路径，避免环境中裸 `dotnet` 命令解析差异。除特别说明外，工作目录为 `E:\GitHub\systemdataup\src`。

| 类别 | 命令 | 工作目录 | 退出码 | 结果 |
|---|---|---|---:|---|
| SDK 信息 | `C:\Program Files\dotnet\dotnet.exe --info` | `E:\GitHub\systemdataup\src` | 0 | SDK 8.0.423，Runtime 8.0.29，Windows x64 |
| Release 构建 | `C:\Program Files\dotnet\dotnet.exe build BackupMonitor.sln -c Release --nologo` | `E:\GitHub\systemdataup\src` | **0** | 8/8 项目构建成功，0 warning，0 error，约 8.88 秒 |
| 现有测试 | `C:\Program Files\dotnet\dotnet.exe test BackupMonitor.sln -c Release --no-build --nologo` | `E:\GitHub\systemdataup\src` | **1** | 总计 59；通过 36；失败 23；跳过 0 |
| Docker 可用性核对 | `docker version --format 'CLIENT={{.Client.Version}} SERVER={{.Server.Version}}'` | `E:\GitHub\systemdataup` | **1** | Client 29.6.2；无法连接 `npipe://./pipe/docker_engine`；服务端为空 |
| Docker 服务核对 | `Get-Service com.docker.service` | `E:\GitHub\systemdataup` | 0 | `COM_DOCKER_SERVICE=MISSING` |
| 前端静态语法核对 | 对 `src/src/BackupMonitor.Api/wwwroot/js` 下 19 个文件逐一执行 `node --check <file>` | `E:\GitHub\systemdataup` | **0** | 19 个文件全部通过，`NODE_CHECK_FAILURES=0` |

### 2.1 测试失败的真实原因

23 个失败项均出现于 `src/tests/BackupMonitor.Infrastructure.Tests/PostgresDatabaseFixture.cs:26` 附近的 Testcontainers PostgreSQL 初始化路径，异常为 `DotNet.Testcontainers.Builders.DockerUnavailableException`，失败端点为 `npipe://./pipe/docker_engine`。本次没有把这些失败改写成“通过”，也没有为了让测试变绿而修改测试或跳过测试。

## 3. 当前客户端和服务端发布物

### 3.1 当前存在的客户端发布物

SHA-256 使用当前文件内容计算；大小为字节数。

| 类型 | 路径 | 存在 | 大小（bytes） | SHA-256 | 说明 |
|---|---|---|---:|---|---|
| 当前 Web 下载安装包 | `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\downloads\BackupMonitor.Agent.Setup.exe` | 是 | 161,733,814 | `16fe19e5a99871c3e7062de2be1d34b9861b644222367261427848762dc94d61` | 当前客户端安装包，LastWrite 2026-08-11 10:20:30 +09:00 |
| 当前 Web 下载 ZIP | `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\downloads\BackupMonitor.Agent.zip` | 是 | 95,977,145 | `c71152084a477ae35d85284f024281415912953280a54dbe80e4b40387ece769` | 当前高级/手工安装包，LastWrite 2026-08-10 16:25:51 +09:00 |
| 发布归档中的安装包副本 | `E:\GitHub\systemdataup\artifacts\BackupMonitor.Agent.Setup-publish\BackupMonitor.Agent.Setup.exe` | 是 | 161,733,814 | `16fe19e5a99871c3e7062de2be1d34b9861b644222367261427848762dc94d61` | 与当前 Web 下载安装包内容一致 |
| 旧归档 ZIP | `E:\GitHub\systemdataup\artifacts\BackupMonitor.Agent.zip` | 是 | 95,977,062 | `7fe2ce53529d36a191295027d48a2d795719bf2c427868cf8290dfb760d767be` | 与当前 Web 下载 ZIP 不同，不作为当前客户端主发布物 |
| 旧归档 ZIP | `E:\GitHub\systemdataup\artifacts\BackupMonitor.Agent-fixed.zip` | 是 | 95,977,062 | `7fe2ce53529d36a191295027d48a2d795719bf2c427868cf8290dfb760d767be` | 与上行旧归档 ZIP 相同 |
| 旧归档 ZIP | `E:\GitHub\systemdataup\artifacts\BackupMonitor.Agent-package-20260809.zip` | 是 | 92,195,415 | `0fa245b99ebcc32ef8183770ea619c8adf301aa74328b8e48aa4ae380f7af85d` | 历史归档，不作为当前客户端主发布物 |

当前 Web 下载 ZIP 的实际条目为：

- `appsettings.json`（531 bytes）
- `BackupMonitor.Agent.exe`（70,055,275 bytes）
- `BackupMonitor.Agent.Tray.exe`（161,831,341 bytes）
- `install-agent.ps1`（4,813 bytes）
- `README.md`（5,383 bytes）
- `uninstall-agent.ps1`（1,570 bytes）

其中该 ZIP 内 `appsettings.json` 的 `RegistrationToken` 为空，未发现非空注册令牌；`AllowUnsignedCommands` 为 `false`。因此不能把该 ZIP 描述为已经完成 LAN 自动入网，只能确认它没有把一个非空注册令牌打包进去。

### 3.2 计划要求的客户端/服务端最终交付物

| 计划要求的路径 | 存在 | 大小 | SHA-256 | 结论 |
|---|---|---:|---|---|
| `E:\GitHub\systemdataup\dist\BackupMonitor.Server.Setup.exe` | 否 | — | — | 当前没有服务端安装 EXE |
| `E:\GitHub\systemdataup\dist\BackupMonitor.Agent.Setup.exe` | 否 | — | — | 当前没有复制到计划要求的 `dist` 路径；现有安装包位于 `wwwroot/downloads` |
| `E:\GitHub\systemdataup\dist\SHA256SUMS.txt` | 否 | — | — | 当前没有最终发布校验清单 |

### 3.3 当前服务端 Release 输出（不是 Turnkey 发布物）

下列文件是 Release 编译输出，仅用于说明当前可编译的服务端形态，不能冒充服务端安装器或最终发布物。

| 路径 | 大小（bytes） | SHA-256 | 说明 |
|---|---:|---|---|
| `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\bin\Release\net8.0\BackupMonitor.Api.exe` | 151,552 | `eab9c84b175ba4b012bbe40188e1100f05a12a14036f378e718195b1c8c3d59f` | framework-dependent Release 输出 |
| `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\bin\Release\net8.0\BackupMonitor.Api.dll` | 131,584 | `2dc4dcbe0f773820d9195422485fed534ff7731401a979bb0af0e3ecb616c7a6` | framework-dependent Release 输出 |

当前 Release 项目还生成了普通的 framework-dependent 客户端编译输出：

- `src/agent/BackupMonitor.Agent/bin/Release/net8.0-windows/BackupMonitor.Agent.exe`：152,064 bytes，SHA-256 `e22e50eb6f2a960d978751a1d6437bd43f774ed9d2b87f20c4e968dacbbbb915`
- `src/agent/BackupMonitor.Agent.Tray/bin/Release/net8.0-windows/BackupMonitor.Agent.Tray.exe`：152,064 bytes，SHA-256 `537e20b06e497769b1330924a85294886c333e1d7f0ca34294b30c716e8f1a0f`
- `src/agent/BackupMonitor.Agent.Setup/bin/Release/net8.0-windows/BackupMonitor.Agent.Setup.exe`：152,064 bytes，SHA-256 `4c2e27664131397e949371108401894ee9f6b5896a013f39b69281cff87ee96c`

这些编译输出与 161 MB 的 Web 安装包不是同一类物件；本次没有重新执行 publish，也没有将普通编译输出误报为最终自包含交付物。

## 4. 提示词 1～12 的真实完成状态

状态含义：

- **代码证据存在**：能在当前源代码、SQL、测试或静态资产中找到对应实现。
- **部分实现**：代码存在，但本次要求的运行、安装、视觉或集成验收没有闭环。
- **阻塞**：需要的验收已执行，但被可复现的外部环境问题阻断。
- **未实现**：当前代码/交付物中没有对应目标；若该目标属于批次 B～G，则本次保持未开始。

| 提示词 | 当前状态 | 实际证据和未闭环项 |
|---|---|---|
| 1. 枚举默认值 | **代码证据存在；运行验收阻塞** | `Enums.cs:18,259` 的枚举零值与 `BackupTask.cs:26,37` 的实体默认值存在；`BackupConfigurations.cs:49-55` 没有枚举成员 `HasDefaultValue`；`EnumDefaultValueRegressionTests.cs:36-137` 覆盖 PostgreSQL 写入/重载和默认值控制。但该测试依赖 Docker PostgreSQL，本次未通过完整运行 gate。 |
| 2. 管理员密码与强制改密 | **代码证据存在；运行验收阻塞** | `V005__admin_password_bootstrap.sql:14-44` 使用外部 `admin_pw` 变量和 bcrypt；`dbinit.bat:3-10,45,63` 要求外部密码；`AuthController.cs:52-60` 提供 `change-password`；`AuthService.cs:205-270` 校验旧密码、强度、bcrypt work factor 12、撤销 refresh token；`auth.js`/`app.js` 有强制改密路由守卫。认证回归测试仍因 Docker fixture 启动失败。 |
| 3. PostgreSQL/Testcontainers/CI | **部分实现；测试 gate 阻塞** | `PostgresDatabaseFixture.cs:26-43` 使用 `postgres:15` 并按顺序执行 V001～V007；测试项目引用 xUnit 和 Testcontainers.PostgreSql；`.github/workflows/ci.yml` 有 CI 构建/测试。但本机 Docker daemon 不可用，23 个测试失败；Windows CI job 也未看到 Setup 项目的发布交付验证。 |
| 4. 只读健康检查与 ZIP 路径安全 | **代码证据存在；运行验收阻塞** | `DatabaseHealthCheck.cs:23-31` 只执行读取查询，`Program.cs:52-61` 注册自定义只读健康检查；`DownloadsController.cs:56-76` 与 `PathSafety.cs:12-75` 做相对路径、目录穿越、UNC/设备路径等校验；`DatabaseHealthCheckTests.cs`、`PathSafetyTests.cs` 覆盖相应边界。依赖数据库的测试本次未完成。 |
| 5. 视觉 token、暗色主题、动效和密度 | **部分实现** | `wwwroot/css/tokens.css`、`base.css`、`components.css` 存在 token、暗色主题、`prefers-reduced-motion`、4px 栅格和紧凑行高等静态实现；本次没有运行浏览器视觉、截图、灰度对比或可访问性验收。 |
| 6. 原生 ES modules/CSS 组件化 | **部分实现** | `wwwroot/index.html:22` 使用 `type="module"`；`wwwroot/js` 19 个模块全部通过 `node --check`；前端没有引入构建链。浏览器加载、交互和布局运行验收本次未执行。 |
| 7. 导航、待办、通知、作业历史、命令面板 | **部分实现** | `app.js:27-43` 有值守/机群/数据/管理四组导航，`app.js:204+` 有命令面板，`app.js:387-429` 有旧 hash 路由和强制改密守卫；`todo.js`、`notifications.js`、相关 job-history 模块存在。只完成了静态和语法核对，未完成浏览器键盘、路由和四组导航运行验收。 |
| 8. 任务摘要/矩阵/仪表盘 | **部分实现** | `AdminReportController.cs:48-53` 暴露 `task-summary`；`ReportDtos.cs:58-123` 和 `ReportService.cs:123+` 有日状态、矩阵、容量和批量查询实现；`dashboard.js:109-137` 有渲染入口。未完成真实数据库查询、性能和浏览器结果验收。 |
| 9. Agent 注册、审批、心跳、命令、扫描、上传和升级 | **部分实现；真实链路阻塞** | `AgentWorker.cs`、`AgentApiClient.cs`、`AgentStateStore.cs`、`BackupScanner.cs`、`install-agent.ps1` 和注册相关 Controller/Service 具备原有 token+approval+mTLS/DPAPI 链路的代码证据。`SetupForm.cs:54,61-62` 仍要求服务端 URL 和一次性注册令牌；没有本次真实安装、审批、心跳、命令、上传、升级端到端验证。 |
| 10. 客户端运行时遥测和告警 | **部分实现；真实链路阻塞** | `V006__client_runtime_telemetry.sql`、`HeartbeatDtos.cs`、`AgentHeartbeatService.cs:52-314`、`AdminClientController.cs:39-49`、`ClientAdminService.cs:151-333` 和 `client-runtime.js` 有指标存储、阈值告警、详情/历史和 UI 代码。没有在可用数据库及运行中的 Agent 上验证持续心跳、指标落库和 UI 展示。 |
| 11. Tray、服务恢复和登录启动 | **部分实现；Windows 运行验收未执行** | `TrayApplicationContext.cs:34-275` 有 NotifyIcon、状态菜单、通知气泡和退出分离；`AgentInstaller.cs` 有 `sc.exe failure`、延迟自动启动和 HKCU Run；Setup 项目目标为 `net8.0-windows`。本次没有在 Windows 服务生命周期、权限、SCM 失败恢复和登录启动环境中实测。 |
| 12. 通知队列、去重、Tray 气泡和品牌 | **部分实现；真实链路阻塞** | `V007__agent_notifications.sql`、`AgentNotificationService.cs`、`AlertingService.cs`、`UploadCommitWorker.cs`、Agent 本地通知队列、Tray 气泡以及 `wwwroot/assets/brand/{mark,wordmark,favicon}.svg` 均存在；静态品牌 SHA-256 已核对，JS 语法已通过。但本次因 Docker 不可用未重跑数据库→心跳→本地队列→Tray 的闭环，也未执行安装后的 Windows 气泡验收。 |

### 4.1 已实现（以代码/静态证据为限）

- 枚举默认值修复方向、认证改密契约、只读健康检查和 ZIP 路径安全均有源代码、SQL 或测试覆盖。
- 原有 Agent 的注册审批、DPAPI 状态、命令签名、扫描、上传、升级和 Windows 安装脚本有对应实现。
- 遥测、告警、通知队列、去重、Tray 气泡和本地 SVG 品牌资产有对应实现。
- 原生 ES modules、CSS token、导航、待办、通知、任务摘要和运行时详情页面有对应静态实现。
- Release 编译通过，19 个前端 JS 文件语法检查通过。

以上“已实现”只表示代码或静态 gate 的结果，不等于数据库集成、浏览器、Windows 安装或完整端到端验收已经通过。

### 4.2 部分实现或验收未闭环

- 提示词 1～4 的 PostgreSQL 回归/集成测试被 Docker daemon 阻塞。
- 提示词 5～8 的前端浏览器运行、视觉、键盘、可访问性和 API/性能验收没有在本次执行。
- 提示词 9～12 的 Agent、服务、Tray、心跳、通知、上传、升级真实运行链没有在本次执行。
- 当前客户端安装器位于 `wwwroot/downloads`，而计划要求的最终交付路径是 `dist`；二者未形成可核验的最终双 EXE 发布目录。

### 4.3 未实现（明确属于当前基线缺口；后续批次尚未开始）

以下项没有在当前仓库/发布目录中找到，且本次按计划没有提前实现：

- 批次 B：`src/src/BackupMonitor.Api/Bootstrap/LocalServerBootstrap.cs` 对应的本地服务自举、ProgramData 持久化服务密钥、环境变量/持久化/生成的优先级和首次初始化页面。
- 批次 C：LanSimple 自动入网、无需用户复制一次性注册令牌的 enrollment/bootstrap 链路。
- 批次 D：UDP `45808` 发现、短期签名 bootstrap 响应和网络边界行为。
- 批次 E：`src/server/BackupMonitor.Server.Setup`、内置 PostgreSQL 管理、迁移 runner、服务安装/维护模式和卸载清理。
- 批次 F：只面向局域网 Turnkey 的服务端/客户端安装体验与配套文档闭环。
- 批次 G：干净 Windows Server/Windows Client VM 上的双击安装、升级、重启恢复、无网/重启/重复安装和安全验收。
- 计划要求的最终交付物 `dist/BackupMonitor.Server.Setup.exe`、`dist/BackupMonitor.Agent.Setup.exe`、`dist/SHA256SUMS.txt`。

这些是按 `LAN-TURNKEY-CHANGE-PLAN.md` 留给后续批次的未开始项，不是本次为了审计而临时扩大范围的修改目标。

### 4.4 阻塞项

1. **Docker daemon 不可用**：`docker version` 无法连接 `npipe:////./pipe/docker_engine`，`com.docker.service` 不存在；因此 23 个依赖 Testcontainers PostgreSQL 的测试失败。需要在 Docker 可用的环境中原命令重跑，才能冻结数据库回归结论。
2. **没有可用的 Windows 服务/干净 VM 验收环境**：本次未执行 Agent Setup、SCM 恢复、HKCU Run、Tray 气泡和重启/断网/重复安装验证。
3. **服务端 Turnkey 交付物不存在**：只有 framework-dependent `BackupMonitor.Api` Release 输出，没有服务端安装器、内置数据库初始化或 `dist` 校验清单。
4. **浏览器视觉和交互验收未执行**：Node 语法通过不代表浏览器渲染、键盘操作、灰度模式或可访问性通过。

## 5. 当前安全/契约边界

本次没有修改业务代码或安全配置，因此没有改变 Secure 现有契约。当前静态证据显示：

- 现有 Agent 仍是服务端 URL + 注册令牌 + 审批 + 证书/签名校验的链路；
- 安装包中的 `RegistrationToken` 为空，未发现把非空令牌打进当前 ZIP 的情况；
- `AllowUnsignedCommands` 为 `false`；
- `V005` 通过外部 `ADMIN_PW` 注入初始管理员密码，而不是把固定密码写入数据库脚本值；
- LAN Turnkey 要求的自动 enrollment 和服务密钥自举尚未实现，本次不以现有 token+approval 链路冒充新契约。

## 6. 批次 B 的唯一建议入口

**唯一建议入口：`src/src/BackupMonitor.Api/Program.cs`。**

批次 B 应从这个服务端启动组合根开始，设计并接入 `src/src/BackupMonitor.Api/Bootstrap/LocalServerBootstrap.cs`，冻结“环境变量 > ProgramData 持久化配置 > 首次生成”的本地服务密钥和初始启动契约，再补充对应测试。除该入口及其直接测试/配置外，本基线不建议提前触碰批次 C～G 的 Agent、Tray、UDP、Setup 或 VM 验收内容。

## 7. 本次实际改动与复核

写入本报告前，`docs/LAN-TURNKEY-BASELINE.md` 不存在。批次 A 完成后，本次代理只新增该文件；以下原有改动保持原样，未执行 reset、checkout、clean、回滚或覆盖操作：

- 已跟踪修改项：`.github/workflows/ci.yml`、`dbinit.bat`、`deploy/README.md`、`docs/DEV-PROMPTS.md`、`docs/OPEN-ISSUES-2026-08-07.md`、`src/BackupMonitor.sln` 以及现有 Api/Core/Infrastructure/Shared/Tests 文件；
- 未跟踪项：`artifacts/`、`src/agent/`、V006/V007、现有注册/引导 Controller、现有前端 assets/css/js/downloads、通知实体/服务和其他原有文件；
- 本报告本身是新增的 `docs/LAN-TURNKEY-BASELINE.md`，不包含批次 B～G 的代码实现。

最终判定：**批次 A 的审计记录已形成；Release 构建通过，但测试和 Turnkey 交付验收未通过/未完成，不能将当前仓库标记为 LAN Turnkey 完成，也不能提前进入批次 B 以后的实现。**

## 附录 A：用户扩大范围后的实施更新（2026-08-11）

本附录不改写上面的批次 A 历史记录。用户随后明确要求“先完成代码，然后继续完成全部测试和开发”，因此已在保留工作区已有改动的前提下继续执行批次 B～G。当前完整证据见 `docs/LAN-TURNKEY-ACCEPTANCE.md`。

- Release 解决方案现在为 9 个项目，构建退出码 `0`，0 warning、0 error。
- 使用本机 PostgreSQL 16 临时实例完成全量测试：73 通过、0 失败、0 跳过，退出码 `0`。Docker Testcontainers 原始环境仍因 daemon 不可用而阻塞，未被改写为通过。
- 19 个 JavaScript 文件逐一 `node --check`，退出码 `0`。
- 最终 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-turnkey.ps1` 退出码 `0`，payload 内容和关键 PostgreSQL 二进制哈希复核退出码 `0`。
- 当前最终服务端安装器：`E:\GitHub\systemdataup\dist\BackupMonitor.Server.Setup.exe`，399,005,992 bytes，SHA-256 `24faad335bfaf36fba90b394fb2b9cd1b9c7fe0affcabcf773d0161db044acae`。
- 当前最终客户端安装器：`E:\GitHub\systemdataup\dist\BackupMonitor.Agent.Setup.exe`，161,867,258 bytes，SHA-256 `e2e74ff58ef2a136572325b70a4f85fc557fd68dc44f42ebe917bf93b5a17d46`。
- 最终 payload 已核实包含 `api/BackupMonitor.Api.exe`、V001/V008 迁移、`postgresql/bin/initdb.exe`、`postgresql/bin/pg_ctl.exe` 和许可证文件；未发现 `server-secrets.json`、PFX 或 `.lock`。
- 批次 B～F 已有代码和开发机测试证据；批次 G 的干净 Windows Server/Windows 11 虚拟机双击安装、登录、Agent 心跳/备份/通知/断网/升级/卸载完整链路仍未执行，因此不宣称“开箱即用”或最终验收通过。
