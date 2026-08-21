# BackupMonitor 局域网一键交付改造方案（交给 Luna 执行）

## 1. 最终交付目标

本系统定位为公司局域网内部使用。最终只向使用者交付两个文件：

1. `BackupMonitor.Server.Setup.exe`：复制到服务器，双击安装；安装后自动启动服务并打开管理网页。
2. `BackupMonitor.Agent.Setup.exe`：从管理网页下载或复制到工作电脑，双击安装；自动发现服务器、自动登记、自动启动服务和托盘。

普通用户不得再接触以下概念：开发密钥、JWT 密钥、RSA 私钥、公钥、CA 口令、数据库连接串、PostgreSQL 账号、一次性注册令牌、注册审批脚本、PowerShell 安装脚本。

唯一需要用户设置的安全信息是“管理网页登录密码”。这不是开发密钥，而是系统管理员日常登录凭据。安装程序还必须提供“维护/重置管理员密码”入口，解决忘记密码的问题。

## 2. 使用者应看到的流程

### 2.1 服务端

1. 双击 `BackupMonitor.Server.Setup.exe`。
2. 只填写管理员密码；安装目录、数据目录和监听端口保留默认值，可在“高级选项”修改。
3. 点击“安装”。安装器自动完成数据库、内部密钥、Windows 服务、防火墙和初始化。
4. 安装完成后自动打开 `http://服务器局域网IP:5080/`。
5. 网页“客户端下载”按钮直接下载 `BackupMonitor.Agent.Setup.exe`。

### 2.2 客户端

1. 双击 `BackupMonitor.Agent.Setup.exe`。
2. 安装器通过局域网发现 BackupMonitor Server；只发现一台时自动选中。
3. 用户只需点击“安装”。如果发现多台服务器才显示选择列表；发现失败时才显示手工填写服务器地址。
4. Agent 自动登记并立即上线，不要求用户复制令牌，也不要求管理员逐台审批。
5. 安装后 Windows 服务自动运行，异常退出由 SCM 自动重启；托盘随当前用户登录启动，只显示异常报警和备份完成消息。

## 3. 关键技术决策

### 3.1 保留 PostgreSQL，不改 SQLite

当前服务端依赖 PostgreSQL 的 `jsonb`、数据库枚举类型、`FOR UPDATE`、`ON CONFLICT ... WHERE`、`RETURNING`、`interval`、`ILIKE` 和 Npgsql 行为。改成 SQLite 会扩散到实体映射、并发、调度锁、幂等、查询和全部数据库测试，风险和返工均明显高于安装器改造。

服务端安装包应内置受支持版本的 PostgreSQL Windows 运行时，安装为本系统专用实例：

- 服务名：`BackupMonitor.PostgreSQL`
- 默认端口：`55432`，避免与用户已有的 5432 实例冲突
- 数据目录：`%ProgramData%\BackupMonitor\PostgreSQL\data`
- 仅监听 `127.0.0.1`
- 数据库：`backup_monitor`
- 应用角色：`backup_monitor_app`
- 随机数据库口令由安装器生成，不显示给用户
- PostgreSQL 版权和许可证文件必须随安装包交付

禁止修改、重置或复用电脑上已有的 PostgreSQL `postgres` 账号和现有数据库实例。

### 3.2 内部密钥首次启动自动生成

新增服务端本地引导模块，在安装或首次启动时生成并持久化：

- JWT 随机签名密钥（至少 32 字节随机量）
- RSA 3072 位指令签名私钥（PKCS#8）
- 客户端 CA PFX 口令
- PostgreSQL 应用角色随机口令
- 服务端实例 ID

保存到 `%ProgramData%\BackupMonitor\Server\server-secrets.json`，使用原子写入；ACL 只允许 `SYSTEM` 和本机 Administrators 读取。不要写入源码、`appsettings*.json`、日志、网页响应或客户端安装包。

明确优先级：显式环境变量 > 已持久化本地密钥 > 首次启动生成。这样保留专业部署的外部配置能力，但普通局域网安装完全不需要配置环境变量。

不要使用仅绑定当前机器、无法迁移恢复的 DPAPI 作为唯一存储。灾难恢复时，服务器数据目录和密钥文件必须可以一起备份并在新服务器恢复。

### 3.3 局域网自动登记，保留高级安全模式

新增配置：

```json
{
  "DeploymentMode": "LanSimple",
  "LanMode": {
    "AutomaticEnrollment": true,
    "PrivateNetworkOnly": true,
    "DiscoveryPort": 45808
  }
}
```

`LanSimple` 是安装器生成的默认部署配置，不在仓库公共 `appsettings.json` 中写入任何机密。

自动登记规则：

- 注册请求可以不带一次性令牌。
- 仅当 `AutomaticEnrollment=true` 且请求直连来源为私有/本地地址时允许免令牌登记。
- 不信任匿名请求提交的 `X-Forwarded-For`；只有来自已配置可信代理时才采用代理头。
- 服务端验证机器 ID、主机名和客户端公钥后，创建客户端、签发客户端证书并直接置为在线。
- 创建客户端、写入证书和审计记录必须处于同一数据库事务。
- 同一机器 ID 重装时不得简单返回 409 形成死循环。应设计安全的“重新登记”流程：旧实例已吊销时允许新建；活动实例存在时返回明确状态，安装器提示管理员在网页执行“重新登记/替换客户端”。不要静默覆盖活动客户端身份。
- 网页仍可禁用、注销和重新登记客户端。
- 现有一次性令牌 + 人工审批流程保留为 `Secure` 高级部署模式，但不出现在默认导航和默认安装流程中。

### 3.4 自动发现服务器

服务端新增 UDP 发现服务，监听 `45808/UDP`。客户端安装器广播包含随机 nonce 的发现请求；服务端响应：

- 原样 nonce
- 服务端实例 ID
- 服务端显示名称
- 管理/API 地址
- 协议版本

安装器只接受 nonce 匹配且协议版本兼容的响应。发现只用于定位服务器，不传输密钥和凭据。实际引导仍通过 HTTP(S) `/api/v1/agent/bootstrap` 获取服务端 RSA 公钥和 Agent 包路径。

安装器行为：发现一台自动选中；发现多台显示列表；10 秒未发现再展示地址输入框。必须允许用户重新扫描。

### 3.5 服务端安装器与维护模式

新增项目建议：

```text
src/server/BackupMonitor.Server.Setup/
  BackupMonitor.Server.Setup.csproj
  Program.cs
  SetupForm.cs
  ServerInstaller.cs
  ServerMaintenance.cs
  app.manifest
```

安装器需管理员权限并完成：

1. 安装应用文件到 `%ProgramFiles%\BackupMonitor\Server`。
2. 创建 `%ProgramData%\BackupMonitor\Server`、数据库和备份仓库目录。
3. 解压并初始化专用 PostgreSQL；注册 `BackupMonitor.PostgreSQL` 自动启动服务。
4. 生成内部配置与密钥，执行 V001 至最新迁移。
5. 以用户输入的管理员密码初始化/重置 `admin`，不得使用固定默认密码。
6. 注册 `BackupMonitor.Server` Windows 服务，设为延迟自动启动，并设置三次失败重启。
7. 添加 TCP 5080 和 UDP 45808 的“专用网络”防火墙规则；不得默认开放到公用网络配置文件。
8. 启动数据库和 API，轮询 `/health` 与数据库就绪状态，成功后打开网页。
9. 安装失败必须回滚本次新建的服务和文件；不得删除升级前已有的数据目录。

同一个安装器再次运行时进入维护模式，提供：

- 修复/升级
- 重启服务
- 重置管理员密码
- 打开管理网页
- 导出诊断包（配置摘要、版本、最近日志；自动脱敏）
- 卸载程序（默认保留数据，可显式选择删除数据并二次确认）

### 3.6 数据库迁移运行器

不能继续依赖用户手工运行 `dbinit.bat`。新增安装器可调用的迁移运行器：

- 建立 `schema_migrations(version, applied_at, checksum)`。
- 按 V001、V002……顺序执行尚未应用的 SQL。
- 每个迁移单独事务；失败则回滚该迁移并停止安装/升级。
- 已应用迁移校验 checksum，文件被改写时拒绝继续并给出清晰错误。
- 管理员密码通过参数/标准输入安全传入，不拼接 SQL，不进入命令行历史和日志。
- 首次安装、升级和修复共用同一套迁移代码。

`dbinit.bat` 可保留为开发工具，但不得出现在普通用户操作说明中。

## 4. 分批执行计划

Luna 必须一次只做一个批次。每批完成后 review diff、构建、测试并提交独立报告，不得把所有改动一次性铺开。

### 批次 A：当前状态审计与契约冻结（只读 + 文档）

目标：确认提示词 1～12 的真实代码状态，建立基线，不修改业务代码。

- 读取 `docs/DEV-PROMPTS.md`、`deploy/README.md`、Agent/Tray/Setup 项目、注册服务、认证、数据库 SQL、发布脚本。
- 执行 `dotnet build BackupMonitor.sln -c Release` 和现有测试。
- 记录当前两个发布物路径、文件哈希、大小、运行依赖和现存失败。
- 输出 `docs/LAN-TURNKEY-BASELINE.md`。

验收：基线文档中的每条“已实现”都有文件或命令证据；失败不得写成通过。

### 批次 B：服务端本地引导与密钥持久化

目标：移除普通安装对 `setup-env.ps1` 和开发密钥的依赖。

重点文件：

- `src/src/BackupMonitor.Api/Program.cs`
- 新增 `src/src/BackupMonitor.Api/Bootstrap/LocalServerBootstrap.cs`
- `src/src/BackupMonitor.Infrastructure/Security/CommandSigner.cs`
- `src/src/BackupMonitor.Infrastructure/Security/CertificateAuthority.cs`
- 配置模型与对应测试

要求：环境变量兼容；持久化后重启密钥不变化；并发首次启动只生成一份；日志不泄密；ACL 测试或可验证脚本齐全。

### 批次 C：局域网自动登记

目标：客户端无令牌提交后自动签证并上线，高级安全模式不回归。

重点文件：

- `RegistrationDtos.cs`
- `AgentRegistrationService.cs`
- `AgentBootstrapController.cs` / `BootstrapDtos.cs`
- `AgentWorker.cs`
- 注册服务集成测试

必须覆盖：LAN 成功、非私有地址拒绝、Secure 模式缺令牌拒绝、无效公钥拒绝、并发同机器 ID、数据库失败不留下半条客户端或孤立证书、现有令牌审批链路继续通过。

### 批次 D：局域网服务发现与客户端安装器简化

目标：客户端默认只剩一个“安装”按钮。

重点文件：

- 新增服务端 UDP discovery hosted service
- `BackupMonitor.Agent.Setup/SetupForm.cs`
- `BackupMonitor.Agent.Setup/AgentInstaller.cs`
- 安装器发现协议测试

删除默认界面的一次性注册令牌字段和审批提示；地址输入改为发现失败后的后备选项。安装完成提示应为“客户端已安装并上线”，不能再要求回网页审批。

### 批次 E：服务端安装器与内置 PostgreSQL

目标：生成真正可复制、双击安装的服务端 EXE。

新增 Server Setup 项目、PostgreSQL payload 管理、迁移运行器、Windows 服务、防火墙和维护模式。不得触碰电脑已有 PostgreSQL 实例。

此批次允许新增明确必要的安装/打包依赖，但必须在报告中说明依赖、许可证、来源、版本和离线构建方式。第三方二进制必须锁定版本并校验 SHA-256。

### 批次 F：网页简化与下载入口

目标：网页首页可以完成日常使用，不暴露部署内部概念。

- “客户端”页主按钮：`下载客户端安装程序`。
- 默认导航隐藏“注册令牌”；高级设置中按需开启。
- 增加“部署状态”小卡片：服务器地址、服务状态、数据库状态、客户端安装器版本。
- 提供简洁的“新电脑怎么装”三步说明。
- 保留客户端详情工作台的 CPU、内存、磁盘、网络、登录会话、Windows 服务和趋势图。

### 批次 G：端到端离线交付验收

必须使用一台干净 Windows Server/Windows 11 虚拟机和至少一台干净 Windows 客户端虚拟机，不得只在开发机上验收。

验收链路：

1. 服务器未安装 .NET、Node、PowerShell 模块或 PostgreSQL。
2. 双击 Server Setup，仅设置管理员密码，安装成功。
3. 重启服务器，两个 Windows 服务自动恢复，网页可访问。
4. 从网页下载 Agent Setup，复制到客户端双击。
5. 自动发现服务器、无令牌安装、客户端在网页自动上线。
6. 网页显示 CPU、内存、磁盘、网络、登录会话和服务状态。
7. 制造一次告警，托盘只显示消息；完成一次备份，托盘只显示完成消息。
8. 强制结束 Agent 进程，SCM 自动重启；托盘退出不影响服务。
9. 断网再恢复，Agent 自动续连且通知不重复。
10. 运行 Server Setup 维护模式重置管理员密码，新密码可登录、旧密码不可登录。
11. 升级安装保留数据库、客户端身份、CA 和历史数据。
12. 卸载 Agent 无残留服务和自启动项；卸载 Server 默认保留数据。

交付物：

- `dist/BackupMonitor.Server.Setup.exe`
- `dist/BackupMonitor.Agent.Setup.exe`
- `dist/SHA256SUMS.txt`
- `docs/局域网部署使用说明.md`（只写双击安装流程，不要求跑脚本）
- `docs/LAN-TURNKEY-ACCEPTANCE.md`（逐项证据、截图、命令输出和哈希）

## 5. 明确禁止事项

- 不得要求最终用户安装 .NET SDK、Node.js、Git、PostgreSQL 或 PowerShell 模块。
- 不得要求最终用户运行 `.ps1`、`.bat`、`dotnet`、`psql` 或环境变量配置命令。
- 不得把固定默认密码或任何真实密钥写入仓库和安装包。
- 不得删除 mTLS、指令签名、哈希校验、路径安全、审计和数据库事务，只能把配置过程自动化。
- 不得为了“一键安装”重置系统已有 PostgreSQL 密码或修改已有数据库。
- 不得把 `/health` 返回 200 当作完整验收；必须验证数据库、登录、Agent 登记、心跳、备份和通知真实链路。
- 不得只生成 ZIP 或脚本后宣称“安装程序已完成”；最终交付必须是可双击的 EXE。
- 不得在未经过干净虚拟机验收时写“开箱即用”。

## 6. Luna 最终报告格式

每批最终报告必须包含：

1. 本批实际改动文件。
2. 用户可见行为变化。
3. 构建、测试和运行命令及退出码。
4. 未通过项和真实阻塞原因。
5. 是否影响现有 Secure 部署模式。
6. 下一批的唯一入口，不得提前实现下一批。

