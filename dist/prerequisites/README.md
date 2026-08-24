# 安装前置条件

本目录里的组件是 BackupMonitor **安装之前**要先装好的东西。
只有一个安装包,但**只有服务端需要**。

## 一句话

| 你要装的 | 要不要先装本目录里的 `vc_redist.x64.exe` |
| --- | --- |
| `BackupMonitor.Server.Setup.exe`(服务端) | **要** |
| `BackupMonitor.Agent.Setup.exe`(客户端 Agent) | 不要 |

## 服务端:VC++ 2015-2022 x64 运行库

```
prerequisites\vc_redist.x64.exe
```

以管理员身份双击运行,按提示装完即可。已经装过会提示"已安装",直接关掉。

**为什么必须装**:服务端内置的 PostgreSQL 是 MSVC 编译的,`postgres.exe` 及若干 DLL
直接导入 `vcruntime140.dll`、`vcruntime140_1.dll`、`msvcp140.dll`。这三个文件
**任何 Windows 版本都不自带**(Server 2016 / 2019 / 2022 也一样),PostgreSQL 自己的
安装目录里也没有。

**漏装的症状很有迷惑性**:安装程序能正常启动、也能一路装完(.NET 自带运行时不依赖
MSVC CRT),但装完之后数据库服务起不来,报的是另一个 DLL 缺失。很容易被当成
"安装成功了但系统有 bug",实际是前置没装。

## 客户端 Agent:不需要任何前置组件

`BackupMonitor.Agent.Setup.exe` 是自包含发布,.NET 运行时打在包里,不依赖 MSVC 运行库。
在系统补丁正常的机器上双击即可。

## 系统补丁

Windows Server 2012 / 2012 R2 需要 `KB2999226`(Universal C Runtime),
且它本身有前置补丁链。**走 Windows Update 全量更新会自动按顺序装好,无需额外操作。**

只有在完全离线、必须手工送补丁的场景下,才需要用到上级目录的
`Install-UcrtPrerequisites.ps1`(它按依赖顺序静默安装 MSU 并判读重启要求)。

> 注意:`vc_redist.x64.exe` **不能替代** `KB2999226`。它把 UCRT 当作由系统补丁提供的
> 前置,自己并不部署——实测在 Server 2012 R2 上装完并重启后,`System32` 里依然没有
> `ucrtbase.dll`。

## 装完之后先自检

上级目录有一份只读自检脚本,不改任何系统设置:

```powershell
powershell -ExecutionPolicy Bypass -File .\Check-Prerequisites.ps1
```

退出码 `0` 表示可以开始安装,`1` 表示还有阻断项。它会逐项报出操作系统版本、
UCRT、VC++ 运行库、进程架构和磁盘余量的实际状态。

`BackupMonitor.Server.Setup.exe` 在点"安装"时也会跑同一套检查并拦下阻断项,
但那是进程已经起来之后的事——UCRT 彻底缺失那种情况只有上面这个脚本能提前发现。

## 校验

`SHA256SUMS.txt` 里是 `vc_redist.x64.exe` 的哈希与版本号。该文件由构建脚本从
<https://aka.ms/vs/17/release/vc_redist.x64.exe> 取得,并已校验其 Authenticode
签名有效且签发者为 Microsoft Corporation。

```powershell
Get-FileHash .\vc_redist.x64.exe -Algorithm SHA256
Get-AuthenticodeSignature .\vc_redist.x64.exe | Format-List Status, SignerCertificate
```
