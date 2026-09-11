# BackupMonitor Agent

## Windows 客户端形态与生命周期

安装包包含两个常驻进程，外加一个只在升级那几分钟里运行的执行器：

- `BackupMonitor.Agent.exe`：Windows Service，使用 LocalSystem 账户运行，负责心跳、配置同步、预检、上传和升级；普通用户不应直接停止它。
- `BackupMonitor.Agent.Updater.exe`：升级执行器，装在安装目录**同级**的 `%ProgramFiles%\BackupMonitor\Updater`。
  Windows 服务不能替换自己正在运行的程序文件，换文件这一步只能由住在覆盖范围之外的程序来做：
  它停服务、把当前安装目录改名做备份、铺新文件、起服务、等心跳恢复，任一步失败立刻回滚旧目录并重新启动服务。
- `BackupMonitor.Agent.Tray.exe`：当前登录用户的通知区域托盘程序，只显示 Agent 服务状态、打开服务端客户端页面和提供退出入口；关闭托盘不会停止采集服务。

安装时会把 Agent 服务设为自动启动/延迟启动，并配置 Windows Service Control Manager 的失败恢复：异常停止后 5 秒、15 秒、60 秒依次重启。正常从托盘选择“正常退出并停止 Agent 服务”时，服务报告正常停止，Windows 不会把它当作故障反复拉起。

托盘会随当前用户登录自动启动。托盘图标颜色含义：绿色为运行中、橙色为启动/停止中、红色为已停止、灰色为无法读取服务状态。双击图标可打开服务端客户端列表。

服务端新打开的异常告警和备份成功入库消息会随下一次心跳下发到托盘，托盘只显示 Windows 气泡提示，不弹出业务窗口。消息按 ID 去重，Agent 重启后不会重复提示；本地消息队列和已见 ID 保存在数据目录的 `tray-notifications.json` 与 `state.json` 中。

“进程不可杀”不能按字面承诺：本机管理员仍然可以使用系统权限强制结束或卸载服务。实际采用的边界是服务与托盘分离、服务使用系统账户、普通用户没有停止权限、异常结束由 SCM 自动恢复，并保留管理员可控的正常退出和卸载路径。

卸载时会删除 Windows 服务、当前用户的托盘自启动项和托盘进程；使用 `-KeepData` 时保留客户端身份、证书和本地状态。

这是 BackupMonitor 的独立 Windows 客户端 Agent。安装后以 Windows Service 运行，负责：

- 使用一次性注册令牌提交机器身份和 RSA 公钥；
- 等待服务器审批并保存 DPAPI 保护的客户端证书；
- 按服务端配置发送心跳、磁盘和关键服务状态；
- 领取预检、重新扫描、配置同步、指标刷新和升级指令；
- 扫描 `latest_single_file`、`latest_directory`、`multi_file_set`、`subdirectory_units` 四类任务；
- 上传预检通过的候选集，支持 SHA-256 校验、分块传输和断点续传。

## 安装（普通用户只需双击）

服务端“客户端”页面提供 `BackupMonitor.Agent.Setup.exe`。将这个安装程序复制到目标 Windows 电脑后，直接双击即可。

安装器会自动：

- 弹出一次 Windows 管理员授权（UAC）；
- 从服务端读取签名公钥并下载 Agent 组件；
- 创建 `BackupMonitor Agent` Windows Service；
- 设置延迟自动启动和异常故障恢复；
- 配置当前用户登录后的托盘自启动；
- 写入受 ACL 保护的一次性注册令牌；
- 启动服务并提交注册申请。

安装向导只需要填写：

- 服务端地址；
- 服务端“注册令牌”页面创建的一次性注册令牌；
- 客户端显示名称（默认使用计算机名）。

安装完成后，管理员仍需在服务端“客户端”页面审批待审批客户端。审批通过后 Agent 会自动领取证书并上线。

如果是批量部署、无人值守部署或旧环境不使用图形安装器，仍可以在解压后的 Agent 目录以管理员身份运行脚本：

```powershell
.\install-agent.ps1 `
  -ServerUrl "https://backup.example.com" `
  -RegistrationToken "一次性令牌" `
  -ServerSigningPublicKey "Base64-SPKI 公钥" `
  -DisplayName "生产备份机-01"
```

脚本和图形安装器使用相同的服务、目录和注册逻辑。注册令牌不会写入 `appsettings.json`，而是写入 `%ProgramData%\BackupMonitor\Agent\registration-token.txt`，文件 ACL 仅授予 LocalSystem 和 Administrators；首次提交注册申请成功后 Agent 会立即删除它。服务端数据库只保存令牌哈希。

图形安装器不是离线包：它需要能够访问填写的服务端地址，以下载经过服务端发布的 Agent 组件。生产环境请使用 HTTPS；开发测试可以使用 HTTP。客户端和服务端不在同一台电脑时，不要填写 `127.0.0.1`，应填写服务端实际域名或地址。

`ServerSigningPublicKey` 必须与服务端 `Security:CommandSigningPrivateKey` 对应（Base64 编码的 RSA SubjectPublicKeyInfo）。Agent 默认拒绝未签名或签名不正确的指令/配置；`AllowUnsignedCommands=true` 只允许本地开发联调，生产安装脚本会强制写为 `false`。升级指令必须提供 HTTPS/HTTP 包地址和 64 位 SHA-256，Agent 下载后先校验哈希再解压。解压只是暂存：Agent 会等到本机空闲（没有指令在执行、没有计划扫描在跑、距离下一次计划扫描还有余量）才把升级执行器拉起来完成切换；切换完成后由**新版本进程**主动回报结果与自己的版本号，服务端据此判成功，而不是拿「指令执行完了」当成功。

## 配置与状态

- 程序目录：`%ProgramFiles%\BackupMonitor\Agent`；
- 状态和证书：`%ProgramData%\BackupMonitor\Agent`；
- `state.json` 中的私钥和 PFX 证书经过 Windows DPAPI（LocalMachine）保护；
- 生产环境必须使用 HTTPS，`AllowInsecureTls` 只用于本地开发联调。

审批通过后，Agent 会自动使用客户端证书访问受保护的 Agent API。服务器开发模式也支持 `X-Client-Id` 回退头，便于本地 HTTP 联调；生产环境该回退必须关闭。

## 卸载

```powershell
.\uninstall-agent.ps1
```

需要保留状态和证书时使用 `-KeepData`。
