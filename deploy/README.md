# 部署说明（nginx 反向代理形态）

```
Agent ──mTLS(443)──┐
                   ├─► nginx ──HTTP──► Kestrel 127.0.0.1:5080
管理员 ──TLS(443)──┘   终结 TLS/mTLS      仅回环监听
```

## 0. 安装前置条件

先在目标机器上跑一遍自检，通过后再运行 `BackupMonitor.Server.Setup.exe`：

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\Check-Prerequisites.ps1
```

脚本只读、不改系统，退出码 0 表示可以继续，1 表示存在阻断项。

| 前置条件 | 要求 |
| --- | --- |
| 操作系统 | Windows Server 2012 及以上，且必须是 64 位 |
| Universal C Runtime | Server 2012 / 2012 R2 必须额外安装（见下） |
| 权限 | 安装程序需以管理员身份运行 |
| VC++ 2015-2022 运行库 | **所有 Windows 版本都需要**，内置 PostgreSQL 依赖它 |
| 磁盘 | 系统盘建议预留 5 GB 以上（服务端 + 内置 PostgreSQL） |

补丁缺失时可用 `deploy/Install-UcrtPrerequisites.ps1` 按依赖顺序批量安装（见下）。

### Universal C Runtime（Server 2012 / 2012 R2 必看）

这两个系统出厂不含 UCRT，而 .NET 8 的 apphost 强依赖它。缺失时的症状是双击安装程序后
弹出系统级错误框：

> 无法启动此程序，因为计算机中丢失 api-ms-win-crt-string-l1-1-0.dll

注意这个弹窗来自 Windows 加载器，进程根本没起来，**安装程序自带的自检也不会执行**，
所以必须靠上面的 PowerShell 脚本在启动前拦截。

**KB2999226 是唯一正规解，VC++ 运行库不能替代。** 实测在 Server 2012 R2 上安装
`vc_redist.x64.exe` 并重启后，`System32` 里依然没有 `ucrtbase.dll`，错误照旧——
该运行库把 UCRT 当作由系统补丁提供的前置，自己并不部署它。

`KB2999226` 本身有前置依赖，**必须按顺序装**，缺前置一律提示"此更新不适用于你的计算机"：

| 系统 | 安装顺序 | KB2999226 包名 |
| --- | --- | --- |
| Server 2012 R2 | `KB2919442` → `KB2919355` → `KB2999226` | `Windows8.1-KB2999226-x64.msu` |
| Server 2012 | `KB2919442` → `KB2999226` | `Windows8-RT-KB2999226-x64.msu` |

`KB2919442` 是服务堆栈更新，`KB2919355` 是 2012 R2 的 Update 1（约 700MB）。
两种系统的包名不通用，选错装不上。

按机器情况选一条路：

1. **能连外网** —— 直接跑完 Windows Update 全量更新。它会自动按依赖顺序把这三个补丁
   都装上，是真正的"一键"，不需要任何额外工具。
2. **只能离线送补丁** —— 从 <https://www.catalog.update.microsoft.com> 按 KB 号下载对应
   的 x64 MSU，放进一个目录，然后：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\deploy\Install-UcrtPrerequisites.ps1 -PackageDirectory D:\msu
   ```

   脚本按文件名识别 KB、校验包与系统平台是否匹配、按依赖顺序静默安装，并判读 `wusa`
   退出码：遇到需要重启就停下来提示，重启后重跑会自动跳过已装项、从断点继续。
   加 `-WhatIf` 只打印安装计划不实际安装。

   注意 2012 R2 的 Update 1 在 Update Catalog 上不是单个包，而是一组
   （`KB2919355` 主包 + `KB2932046`/`KB2934018`/`KB2937592`/`KB2938439`/`KB2959977`），
   组内也有固定顺序。脚本已内置这个顺序，目录里没有的组件按可选跳过——
   很多机器已经通过 Windows Update 装过其中一部分。硬前置只有 `KB2919442`、
   `KB2919355`、`KB2999226` 三个，缺了会直接报错退出。

排查补丁链断在哪一环：

```powershell
Get-HotFix | Where-Object { $_.HotFixID -in 'KB2919442','KB2919355','KB2999226' } |
    Select-Object HotFixID, InstalledOn
```

注意 `Get-HotFix` 读的是 `Win32_QuickFixEngineering`，查不到以 CBS 组件方式安装的包，
`KB2919355` 尤其常见漏报。要确认得用 DISM 复核：

```powershell
dism /online /get-packages | Select-String 2919355
```

最直接的判据其实是 `Test-Path $env:SystemRoot\System32\ucrtbase.dll` —— 它为 `True`
就说明 UCRT 已就位。

装完重启，再跑一次自检脚本确认。

> **生命周期提醒**：Server 2012 与 2012 R2 的扩展支持已于 2023-10 结束，.NET 9 及以后版本
> 不再支持这两个系统。本项目当前基于 .NET 8 尚可运行，但后续升级框架时会撞墙，
> 请提前规划迁移到 Server 2016 及以上。

### VC++ 2015-2022 运行库（所有版本都要）

内置的 PostgreSQL 是 MSVC 编译的，`postgres.exe` 及若干 DLL 直接导入
`vcruntime140.dll`、`vcruntime140_1.dll`、`msvcp140.dll`。这三个**任何 Windows 版本都不自带**
（2016/2019/2022 也一样），`pgsqlin` 里也没有，必须装
<https://aka.ms/vs/17/release/vc_redist.x64.exe>。

缺它不影响安装程序启动（.NET 自带运行时不依赖 MSVC CRT），但装完后数据库服务起不来，
报的是另一个 DLL 缺失，容易被当成新问题。

顺序上要注意：在缺 UCRT 的老系统上，这个安装包装不进任何东西。**先打完 KB2999226 并重启，
再重新运行一次 `vc_redist.x64.exe`**。

### 安装程序自带的自检

`BackupMonitor.Server.Setup.exe` 在点击"安装"时会先跑一遍同样的检查
（`PrerequisiteCheck`）：阻断项直接拦下安装，警告项弹窗让操作员确认后才继续；
"运行诊断"按钮的输出末尾也会附上自检结果，便于远程排障。它的覆盖范围止于
"进程已经起来了"之后——UCRT 彻底缺失那种情况只有前置脚本能发现。

## 1. 注入机密（首次或轮换后）

机密不再写入任何 `appsettings*.json`，全部走计算机级环境变量：

| 环境变量 | 对应配置键 |
| --- | --- |
| `ConnectionStrings__Default` | `ConnectionStrings:Default` |
| `Security__Jwt__SigningKey` | `Security:Jwt:SigningKey` |
| `Security__CommandSigningPrivateKey` | `Security:CommandSigningPrivateKey`（Base64 PKCS#8 RSA 私钥） |
| `Security__AllowLegacyCommandHmac` | `Security:AllowLegacyCommandHmac`（仅迁移期显式开启，生产必须 `false`） |
| `Security__ClientCa__Password` | `Security:ClientCa:Password` |

以管理员身份运行仓库根目录的 `setup-env.ps1`（该文件已被 `.gitignore` 排除），然后重启服务。

Agent 安装包还必须配置与该 RSA 私钥匹配的 Base64 SPKI 公钥（`install-agent.ps1 -ServerSigningPublicKey`）。服务端下发的指令和配置均使用 RSA-SHA256 签名，Agent 默认拒绝未签名内容。

首次生成密钥对可使用 PowerShell（私钥只交给服务端，公钥放入 Agent 安装参数）：

```powershell
$rsa = [Security.Cryptography.RSA]::Create(3072)
[Convert]::ToBase64String($rsa.ExportPkcs8PrivateKey()) | Set-Content .\command-signing-private.b64
[Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) | Set-Content .\command-signing-public.b64
$rsa.Dispose()
```

数据库初始化使用仓库根目录 `dbinit.bat`：迁移由 `MigrationRunner` 按 `schema_migrations` 记录逐个补齐（当前 V001~V014），已执行过的版本自动跳过，重复运行安全。

## 2. 证书

需要两套证书，用途不同，**不要混用**：

- **服务端证书** `server.crt` / `server.key` —— nginx 对外提供 HTTPS，需浏览器信任。
- **客户端 CA** `client-ca.crt` —— 用于签发和校验 Agent 证书。必须与应用的
  `Security:ClientCa:CertPath`（PFX，含私钥）是同一张 CA：应用用它签发，nginx 用它校验。

`Security:ClientCa:CertPath` 留空时，应用会在 `data/dev-ca.pfx` 自动生成一张开发 CA，
口令取自 `Security__ClientCa__Password`。导出其公钥部分给 nginx：

```bash
openssl pkcs12 -in data/dev-ca.pfx -clcerts -nokeys -out client-ca.crt
```

正式环境应替换为真实 CA。

## 3. nginx

把 `nginx/backup-monitor.conf` 放入 `conf.d/`，改掉 `server_name` 与证书路径后 `nginx -t && nginx -s reload`。

## 4. 关键约束

- **Kestrel 只能监听 `127.0.0.1`。** 监听 `0.0.0.0` 会让人绕过 nginx 直连明文端口，
  同时跳过 TLS 与客户端证书校验——等于前面所有防护都不存在。
- `Security:ClientAuth:ForwardedProxies` 必须包含 nginx 的来源地址（同机为 `127.0.0.1` / `::1`），
  它同时决定了 `X-Forwarded-For` 与 `X-Client-*` 头是否被采信。留空即全部不采信。
- `Security:ClientAuth:AllowDevelopmentHeader` 生产必须为 `false`。

## 5. 验证

```bash
# 健康检查（匿名）
curl -k https://backup.example.internal/health

# 无客户端证书访问 Agent 接口 → 预期 401
curl -k https://backup.example.internal/api/v1/agent/config

# 带客户端证书 → 预期 200
curl -k --cert agent.crt --key agent.key \
     https://backup.example.internal/api/v1/agent/config

# 伪造证书头（不经 nginx，直连回环）→ 预期 401 且日志出现"疑似伪造"告警
curl -H 'X-Client-Verify: SUCCESS' -H 'X-Client-Fingerprint: aabb...' \
     http://127.0.0.1:5080/api/v1/agent/config
```

最后一条只有在 Kestrel 错误地监听了对外地址时才可能从外部发起——它同时是一次配置自检。
