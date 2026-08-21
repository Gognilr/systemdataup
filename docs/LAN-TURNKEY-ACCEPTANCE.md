# LAN Turnkey 批次 B～G 实施与验收报告

日期：2026-08-11（Asia/Tokyo）  
仓库：`E:\GitHub\systemdataup`  
基线：`docs/LAN-TURNKEY-BASELINE.md`  
执行依据：`docs/DEV-PROMPTS.md`、`docs/LAN-TURNKEY-CHANGE-PLAN.md`

## 0. 结论先行

批次 B～F 的代码、测试和 Release 发布链路已经完成；开发机上的真实 PostgreSQL 集成测试为 **73/73 通过、0 失败、0 跳过**，Release 解决方案为 **9/9 项目、0 警告、0 错误**，前端 19 个 JavaScript 文件全部通过 `node --check`，最终双 EXE 和服务端 payload 已生成并通过哈希/内容核对。

批次 G 的“干净 Windows Server/Windows 11 虚拟机双击安装和完整业务链路”尚未执行，因此本报告不写“开箱即用”或“最终验收通过”。当前剩余的主要阻塞是没有可用于破坏性安装、重启、卸载和跨机器联调的干净虚拟机验收环境；浏览器视觉/交互验收也未执行。

此前 `docs/LAN-TURNKEY-BASELINE.md` 中的批次 A 历史结论保持不改。本报告记录的是在用户明确扩大范围后完成的 B～G 实施和当前证据。

> 与 2026-08-13 审查的回链：审查报告中的 #1（见 [`REVIEW-2026-08-13.md`](./REVIEW-2026-08-13.md)）曾被“E2E 尚未闭环”的历史状态掩盖；本次已用最小真实请求确认无客户端证书时返回 401，并按批次 1 完成 HTTPS/mTLS 修复。干净虚拟机和浏览器验收仍按本报告的未闭环项单独保留。

## 1. 实际检查的文件

### 1.1 规范、基线和工作区

- `E:\GitHub\systemdataup\docs\DEV-PROMPTS.md`（完整读取）
- `E:\GitHub\systemdataup\docs\LAN-TURNKEY-CHANGE-PLAN.md`（完整读取）
- `E:\GitHub\systemdataup\docs\LAN-TURNKEY-BASELINE.md`
- `E:\GitHub\systemdataup\src\BackupMonitor.sln`
- `E:\GitHub\systemdataup\scripts\build-turnkey.ps1`
- `E:\GitHub\systemdataup\deploy\README.md`
- `E:\GitHub\systemdataup\.github\workflows\ci.yml`

### 1.2 批次 B：服务端本地引导和密钥

- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\Program.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\Bootstrap\LocalServerBootstrap.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Shared\Security\LanNetworkPolicy.cs`
- `E:\GitHub\systemdataup\src\tests\BackupMonitor.Infrastructure.Tests\LocalServerBootstrapTests.cs`

检查重点：环境变量/持久化/首次生成优先级、原子写入、跨进程锁、CA 生成、ProgramData 路径、Windows ACL、日志和响应不泄密。

### 1.3 批次 C：自动登记、事务和证书

- `E:\GitHub\systemdataup\src\src\BackupMonitor.Infrastructure\Services\AgentRegistrationService.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Infrastructure\Security\CertificateAuthority.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Shared\Models\Agent\RegistrationDtos.cs`
- `E:\GitHub\systemdataup\src\database\V008__lan_turnkey_enrollment.sql`
- `E:\GitHub\systemdataup\src\tests\BackupMonitor.Infrastructure.Tests\AgentRegistrationTurnkeyTests.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\Controllers\AgentRegistrationController.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\Controllers\AgentBootstrapController.cs`

检查重点：私网免令牌登记、Secure 模式令牌保护、活动身份冲突、吊销/禁用重登记、客户端证书、审计记录和数据库事务。

### 1.4 批次 D：服务发现和客户端安装器

- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\Discovery\LanDiscoveryHostedService.cs`
- `E:\GitHub\systemdataup\src\agent\BackupMonitor.Agent.Setup\LanDiscoveryClient.cs`
- `E:\GitHub\systemdataup\src\agent\BackupMonitor.Agent.Setup\SetupForm.cs`
- `E:\GitHub\systemdataup\src\agent\BackupMonitor.Agent.Setup\AgentInstaller.cs`
- `E:\GitHub\systemdataup\src\agent\BackupMonitor.Agent.Setup\app.manifest`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Shared\Models\Agent\LanDiscoveryDtos.cs`
- `E:\GitHub\systemdataup\src\tests\BackupMonitor.Infrastructure.Tests\LanDiscoveryTests.cs`

检查重点：UDP 45808、私网来源、nonce、只返回公开元数据、发现失败后备地址、自动登记、服务失败恢复、托盘自启动和原生卸载。

### 1.5 批次 E：服务端安装器和 PostgreSQL payload

- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\BackupMonitor.Server.Setup.csproj`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\app.manifest`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\Program.cs`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\ProcessRunner.cs`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\MigrationRunner.cs`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\PostgreSqlManager.cs`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\ServerInstaller.cs`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\ServerMaintenance.cs`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\payload\server-payload.zip`
- `E:\GitHub\systemdataup\src\database\V001__initial_schema.sql` 至 `V008__lan_turnkey_enrollment.sql`

检查重点：自包含 Win-x64 EXE、专用 PostgreSQL 服务/端口/目录、迁移 checksum、管理员密码参数化、Windows 服务、专用网络防火墙、健康检查、回滚、维护和卸载安全路径。

### 1.6 批次 F：管理端部署入口和 UI

- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\Controllers\AdminDeploymentController.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Shared\Models\Admin\DeploymentStatusDtos.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\js\app.js`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\js\views\clients.js`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\css\components.css`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\index.html`

检查重点：默认客户端页主按钮、默认导航隐藏注册令牌、部署状态卡片、三步安装说明和原有客户端运行时工作台保留。

### 1.7 测试和发布物

- `E:\GitHub\systemdataup\src\tests\BackupMonitor.Infrastructure.Tests\PostgresDatabaseFixture.cs`
- `E:\GitHub\systemdataup\src\tests\BackupMonitor.Infrastructure.Tests\RetentionCleanupWorkerTests.cs`
- `E:\GitHub\systemdataup\src\tests\BackupMonitor.Infrastructure.Tests\LocalServerBootstrapTests.cs`
- `E:\GitHub\systemdataup\src\tests\BackupMonitor.Infrastructure.Tests\LanDiscoveryTests.cs`
- `E:\GitHub\systemdataup\src\tests\BackupMonitor.Infrastructure.Tests\AgentRegistrationTurnkeyTests.cs`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\downloads\BackupMonitor.Agent.Setup.exe`
- `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\downloads\BackupMonitor.Agent.zip`
- `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\payload\server-payload.zip`
- `E:\GitHub\systemdataup\dist\BackupMonitor.Server.Setup.exe`
- `E:\GitHub\systemdataup\dist\BackupMonitor.Agent.Setup.exe`
- `E:\GitHub\systemdataup\dist\SHA256SUMS.txt`
- `E:\GitHub\systemdataup\dist\payload-manifest.json`

工作区原有改动未被 reset、checkout、clean、覆盖或回滚；未执行 git commit/push。

## 2. 构建、测试和发布命令及退出码

以下命令均在 `E:\GitHub\systemdataup` 执行，使用显式工具路径。

| 命令 | 退出码 | 真实结果 |
|---|---:|---|
| `C:\Program Files\dotnet\dotnet.exe build .\src\BackupMonitor.sln -c Release --nologo` | 0 | 9 个项目成功，0 警告，0 错误 |
| `$env:BACKUPMONITOR_TEST_LOCAL_POSTGRES='1'; $env:BACKUPMONITOR_POSTGRES_ROOT='C:\Program Files\PostgreSQL\16'; dotnet test .\src\tests\BackupMonitor.Infrastructure.Tests\BackupMonitor.Infrastructure.Tests.csproj -c Release --nologo` | 0 | 73 通过，0 失败，0 跳过；使用本机 PostgreSQL 16 临时实例，测试结束后停止临时实例 |
| `dotnet test ... --filter FullyQualifiedName~AgentRegistrationTurnkeyTests` | 0 | 自动登记专项 5/5 通过 |
| `dotnet test ... --filter FullyQualifiedName~LanDiscoveryTests` | 0 | UDP 发现专项 1/1 通过 |
| 对 `src\src\BackupMonitor.Api\wwwroot\js` 19 个文件逐一执行 `C:\Program Files\nodejs\node.exe --check <file>` | 0 | 19/19 JavaScript 语法通过 |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-turnkey.ps1 -Configuration Release -Runtime win-x64`（2026-08-13 重新运行） | 0 | 重新生成双 EXE、内置 API/迁移/PostgreSQL payload 和 SHA 清单 |
| 最终 payload/SHA 校验脚本：核对 SHA256SUMS、V001/V008、客户端包、`initdb.exe`/`pg_ctl.exe` 及 manifest 哈希 | 0 | 校验通过，payload 2,139 个条目 |
| 基线中的 `docker version --format ...` | 1 | Docker daemon 不可用；没有把 Docker Testcontainers gate 写成通过 |

全量测试使用的是真实 PostgreSQL 临时实例回退路径，不是内存数据库。原始 Testcontainers 路径仍保留；由于当前开发机 Docker daemon 不可用，Docker 专用环境本身仍需另行重跑。

打包脚本曾在迁移/运行时复制校验中发现并修复了两个真实问题：SQL 通配符不能与 `-LiteralPath` 一起使用、PostgreSQL `bin/lib/share` 不能用同样的错误复制方式。也曾有路径同步尝试退出码 1；该非必要副作用已移除，最终打包运行以退出码 0 完成，失败产物没有作为交付物使用。

## 3. 当前客户端和服务端发布物

SHA-256 为最终文件当前内容计算值，大小单位为 bytes。

| 类型 | 路径 | 大小 | SHA-256 |
|---|---|---:|---|
| 服务端最终安装器 | `E:\GitHub\systemdataup\dist\BackupMonitor.Server.Setup.exe` | 397,937,960 | `68dbeaa46602c452526fb613c057e19ec4342caf516311c35d3942f6324aa435` |
| 客户端最终安装器 | `E:\GitHub\systemdataup\dist\BackupMonitor.Agent.Setup.exe` | 161,876,474 | `a2dac5c25c1728938f1649581ae742a41b9abdf964c0a14c8eb0195528a8b28b` |
| SHA 清单 | `E:\GitHub\systemdataup\dist\SHA256SUMS.txt` | 195 | 与上面两个 EXE 一致 |
| 服务端 payload | `E:\GitHub\systemdataup\src\server\BackupMonitor.Server.Setup\payload\server-payload.zip` | 166,448,743 | SHA-256 `67f592a0061a476ce2bb271e83fea0d33c429f2dce3f285ce6d3b3bd5dc329dc`；已检查内容；非最终用户交付文件 |
| Web 客户端安装器 | `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\downloads\BackupMonitor.Agent.Setup.exe` | 161,867,258 | `e2e74ff58ef2a136572325b70a4f85fc557fd68dc44f42ebe917bf93b5a17d46` |
| Web 高级兼容 ZIP | `E:\GitHub\systemdataup\src\src\BackupMonitor.Api\wwwroot\downloads\BackupMonitor.Agent.zip` | 138,638 | `0a5df781d424381b6aa848ff02ca6e66271c42644847bc5cd7236af4ec9c83fc` |

`payload-manifest.json` 锁定 PostgreSQL `16.14`、运行时 `win-x64`，并记录：

- `initdb.exe` SHA-256：`2111500f1faa10211d330c2bfd88afedc00abe9caa134607c776453ff6f46f09`
- `pg_ctl.exe` SHA-256：`b6656121ef0a417ca4bd46dbe3ae50fd9ae077d8d6c06f92f9d498c224488c48`

最终 payload 内容核验确认存在：

- `api/BackupMonitor.Api.exe`
- `api/wwwroot/downloads/BackupMonitor.Agent.Setup.exe`
- `database/V001__initial_schema.sql`
- `database/V008__lan_turnkey_enrollment.sql`
- `postgresql/bin/initdb.exe`
- `postgresql/bin/pg_ctl.exe`
- PostgreSQL 许可证/版权文件

核验没有发现 `server-secrets.json`、PFX、`.lock` 或运行时生成的内部密钥文件进入 payload。客户端默认下载安装器是自包含单文件 EXE；兼容 ZIP 仅保留在高级/兼容路径，默认网页主按钮下载 EXE。

## 4. B～G 批次真实状态

| 批次 | 状态 | 已完成证据 | 未闭环项 |
|---|---|---|---|
| B 本地服务引导 | **代码实现 + 测试通过；环境验收未闭环** | `LocalServerBootstrap`、原子 secrets、CA、ACL、优先级和 8 个专项测试 | 尚未在干净服务器上首次双击安装并复启验证 |
| C LAN 自动登记 | **代码实现 + PostgreSQL 集成测试通过；E2E 未闭环** | 5 个专项测试覆盖 LAN 成功、非私网拒绝、Secure 缺令牌、非法公钥回滚、并发身份冲突 | 尚未在真实 API + Agent Setup + 两台干净机器上完成登记/心跳 |
| D LAN 发现/安装简化 | **代码实现 + UDP 测试通过；Windows 安装未闭环** | 服务端 hosted service、客户端 nonce 校验、发现失败后备地址、Secure 高级令牌兼容、原生客户端卸载 | 未在真实局域网广播、防火墙和双击安装环境中执行 |
| E 服务端安装器 | **代码实现 + Release/payload 通过；双击验收未闭环** | Server Setup、专用 PostgreSQL、迁移 checksum、服务/防火墙/维护/回滚路径，payload 内容和哈希 | 未在没有 .NET/Node/PostgreSQL 的干净 VM 中安装、重启、升级、卸载 |
| F 网页简化 | **代码实现 + JS 语法通过；浏览器验收未闭环** | 下载 EXE 主入口、默认隐藏令牌、部署状态卡、三步说明、原客户端详情保留 | 未执行浏览器交互、截图、键盘/可访问性和真实 API 状态渲染验收 |
| G 离线交付验收 | **未完成，明确阻塞** | 最终 EXE、SHA、payload 和使用说明已具备 | 缺干净 Windows Server/Windows 11 VM；未完成完整安装、登录、心跳、备份、通知、断网、重启、升级和卸载链路 |

## 5. 提示词 1～12 当前真实完成状态

| 提示词 | 状态 | 当前证据和剩余项 |
|---|---|---|
| 1 枚举默认值 | **已实现；Docker 专用 gate 未执行** | 现有回归测试在本机真实 PostgreSQL 回退路径通过；Docker daemon 不可用的原始环境阻塞仍保留记录 |
| 2 管理员密码/强制改密 | **已实现；干净安装登录未执行** | V005 参数化管理员密码、认证改密与 refresh token 吊销代码及数据库测试存在；尚未在 Server Setup 首次安装后实测网页登录 |
| 3 PostgreSQL/Testcontainers/CI | **部分实现；Docker gate 阻塞** | 本机临时 PostgreSQL 全量 73/73 通过，Testcontainers 路径和 CI 仍存在；当前机器没有 Docker daemon |
| 4 只读健康检查/ZIP 路径安全 | **代码和测试已实现；部署运行未执行** | 只读 DB health、路径安全和迁移/包校验均有证据；未在安装后的服务端真实网页执行 |
| 5 视觉 token/暗色/动效/密度 | **代码部分实现** | CSS token、暗色、减弱动效和密度资产存在；无浏览器截图/视觉/可访问性证据 |
| 6 ES modules/CSS 组件化 | **代码实现；浏览器 gate 未执行** | 19 个 JS 文件 `node --check` 全通过；未执行浏览器加载和交互 |
| 7 导航/待办/通知/作业/命令面板 | **代码部分实现** | 静态模块、导航和路由存在；未完成浏览器键盘、权限和真实 API 交互验收 |
| 8 任务摘要/矩阵/仪表盘 | **代码部分实现** | Controller、DTO、Service 和 dashboard 渲染存在；未在真实部署数据库和浏览器完成性能/结果验收 |
| 9 Agent 注册/审批/心跳/命令/扫描/上传/升级 | **代码实现 + 注册专项测试；端到端未闭环** | 原有 Secure 链路和新增 LAN 登记、EXE 安装器均存在；未在干净客户端完成全链路安装、心跳、命令、上传、升级 |
| 10 运行时遥测/告警 | **代码部分实现；真实 Agent 链路未验证** | SQL、服务和客户端详情 UI 存在；未在安装后的 Agent 上验证持续指标、阈值和页面展示 |
| 11 Tray/SCM 恢复/登录启动 | **代码实现；Windows 生命周期未验证** | Tray、SCM failure actions、HKCU Run、原生卸载入口已实现；未在 VM 中强杀、重启、登录和卸载实测 |
| 12 通知队列/去重/气泡/品牌 | **代码部分实现；端到端未验证** | 通知队列、去重、Tray 气泡和品牌资源存在；未完成告警→队列→托盘和备份完成消息的真实 Windows 验收 |

## 6. 已实现、部分实现、未实现和阻塞项

### 已实现并有当前命令证据

- Release 解决方案编译：9/9、0 warning、0 error。
- 本机真实 PostgreSQL 回退路径全量测试：73/73 通过。
- LAN 服务发现回环测试：1/1 通过。
- LAN 自动登记 PostgreSQL 集成测试：5/5 通过。
- 前端 JavaScript 语法：19/19 通过。
- 双自包含 Win-x64 Setup EXE、SHA256SUMS 和 PostgreSQL payload。
- 服务端本地密钥/CA 自举、专用 PostgreSQL、迁移 checksum、服务/防火墙/维护/回滚代码。
- 客户端 LAN 发现、自动登记、Secure 高级令牌兼容和原生卸载代码。
- 网页下载入口、部署状态卡和三步说明。

### 部分实现

- Docker Testcontainers 专用运行环境尚未恢复；本次使用真实本机 PostgreSQL 临时实例完成了等价数据库测试，但没有把 Docker 不可用写成通过。
- 浏览器视觉、键盘、可访问性和真实 API 页面状态没有执行。
- 双机 LAN、Windows SCM、托盘、断网续连、备份、通知和升级没有在实际安装后执行。
- 服务端/客户端安装器目前只做了代码和 payload 内容校验，没有实际双击运行安装器后的数据库、登录、心跳和业务链路证据。

### 未实现/未完成验收

- 批次 G 要求的干净 Windows Server/Windows 11 VM 逐项验收和截图。
- “服务器无 .NET/Node/PostgreSQL 仍可双击完成安装”的实机证据。
- 安装后的管理员登录、Agent 自动登记、心跳、遥测、告警、备份、通知、断网恢复、升级保留数据和卸载无残留的完整证据链。
- 浏览器 UI 的最终视觉和可访问性验收记录。

### 阻塞项

1. 当前没有可安全反复重启/卸载的干净 Windows Server/Windows 11 虚拟机；在开发机上直接运行服务端安装器会改变现有服务、防火墙和 ProgramData，未获得这样的外部验收环境，因此不执行破坏性替代。
2. 当前 Docker daemon 不可用，`docker version` 基线退出码为 1；原 Testcontainers 运行路径需在 Docker 可用环境重跑。
3. 没有浏览器视觉验收会话，因此 Node 语法通过不能等价替代页面验收。

## 7. 批次 B 的唯一建议入口

**唯一建议入口：`E:\GitHub\systemdataup\src\src\BackupMonitor.Api\Bootstrap\LocalServerBootstrap.cs`。**

该文件是批次 B 的本地密钥、CA、ProgramData 和启动配置契约的唯一维护入口；`LocalServerBootstrapTests.cs` 只作为其直接测试证据，不扩展为第二个实施入口。后续若继续做 B 的审阅或修订，应从这里检查环境变量 > 持久化文件 > 首次生成、原子写入、ACL、CA 恢复和日志不泄密边界。

## 8. 交付文件

- [服务端安装器](../dist/BackupMonitor.Server.Setup.exe)
- [客户端安装器](../dist/BackupMonitor.Agent.Setup.exe)
- [SHA-256 清单](../dist/SHA256SUMS.txt)
- [局域网部署使用说明](./局域网部署使用说明.md)
- [批次 A 基线](./LAN-TURNKEY-BASELINE.md)
