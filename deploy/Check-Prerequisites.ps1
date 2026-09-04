<#
.SYNOPSIS
    BackupMonitor 服务端/Agent 安装前的环境自检。

.DESCRIPTION
    在运行 BackupMonitor.Server.Setup.exe 之前执行。安装程序自身也带一份自检，
    但那份跑在 .NET 里：当机器缺少 Universal C Runtime 时，exe 会被 Windows 加载器
    直接拦下（弹窗“丢失 api-ms-win-crt-string-l1-1-0.dll”），托管代码没有机会运行。
    因此 UCRT 这一类前置条件只能由本脚本在启动前检查。

    本脚本只读，不修改系统。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Check-Prerequisites.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$script:Failures = 0
$script:Warnings = 0

function Write-Result {
    param(
        [ValidateSet('OK', 'WARN', 'FAIL')][string]$Level,
        [string]$Title,
        [string]$Detail
    )
    $color = switch ($Level) { 'OK' { 'Green' } 'WARN' { 'Yellow' } 'FAIL' { 'Red' } }
    Write-Host ("[{0,-4}] {1}" -f $Level, $Title) -ForegroundColor $color
    if ($Detail) {
        foreach ($line in $Detail -split "`n") { Write-Host ("       " + $line.TrimEnd()) }
    }
    if ($Level -eq 'FAIL') { $script:Failures++ }
    if ($Level -eq 'WARN') { $script:Warnings++ }
}

# UCRT 在 Server 2012 / 2012 R2 上只能靠 KB2999226 补齐：
# 报错点名的 api-ms-win-crt-*.dll 是一组转发 DLL，只有这个系统补丁会把它们装进 System32。
# vc_redist.x64.exe 在这两个系统上不部署 UCRT（它把 UCRT 当作由 OS 补丁提供的前置），
# 实测装完并重启后 System32 里连 ucrtbase.dll 都不会出现，因此它替代不了补丁。
# 补丁自身的前置链又随版本不同：
#   Server 2012 R2：KB2919442（服务堆栈）→ KB2919355（Update 1）→ KB2999226
#   Server 2012   ：KB2919442（服务堆栈）→ KB2999226
# 顺序不对或包选错，安装时一律报"此更新不适用于你的计算机"。
function Get-UcrtGuidance {
    param([Version]$OsVersion)

    $steps = if ($OsVersion -ge [Version]'6.3') {
        @'
    1. KB2919442（服务堆栈更新）
    2. KB2919355（Update 1，包较大约 700MB）
    3. KB2999226 —— 包名 Windows8.1-KB2999226-x64.msu（勿选 Windows8-RT-*）
'@
    } else {
        @'
    1. KB2919442（服务堆栈更新）
    2. KB2999226 —— 包名 Windows8-RT-KB2999226-x64.msu（勿选 Windows8.1-*）
'@
    }

    $osName = if ($OsVersion -ge [Version]'6.3') { 'Server 2012 R2' } else { 'Server 2012' }

    return @"
唯一正规解：安装系统补丁 KB2999226（$osName）。
  必须按顺序安装，缺前置会提示"此更新不适用于你的计算机"：
$steps
  前两步通常在跑完 Windows Update 全量更新后已就位。
  下载：https://www.catalog.update.microsoft.com/Search.aspx?q=KB2999226

注意：安装 VC++ 2015-2022 运行库（vc_redist.x64.exe）不能替代该补丁。
  它在本系统上不部署 UCRT，装完重启后 System32 里依然没有 ucrtbase.dll，
  错误照旧。

应急手段（无法打补丁时）：把已打补丁机器 System32 下的 ucrtbase.dll 与全部
  api-ms-win-crt-*.dll 复制到 exe 同目录（应用本地部署）。仅够让安装程序跑起来，
  装完的服务端不在该目录下运行，仍需正式补丁。
"@
}

Write-Host "BackupMonitor 安装前环境自检" -ForegroundColor Cyan
Write-Host ("=" * 60)

# --- 操作系统版本 ---------------------------------------------------------
$os = Get-WmiObject -Class Win32_OperatingSystem
$osVersion = [Version]$os.Version
$caption = $os.Caption.Trim()

if ($osVersion -lt [Version]'6.2') {
    Write-Result FAIL "操作系统版本过低：$caption（$osVersion）" `
        ".NET 8 最低要求 Windows Server 2012 / Windows 8，本机无法安装。"
}
elseif ($osVersion -lt [Version]'6.3') {
    Write-Result WARN "$caption（$osVersion）" `
        ("该系统出厂不含 Universal C Runtime，必须额外打补丁（见下方 UCRT 检查）。`n" +
         "另：微软对它的扩展支持已于 2023-10 结束，.NET 9 及以后版本不再支持该系统，`n" +
         "建议规划迁移到 Server 2016 及以上。")
}
elseif ($osVersion -lt [Version]'10.0') {
    Write-Result WARN "$caption（$osVersion）" `
        ("该系统出厂同样不含 Universal C Runtime，必须额外打补丁（见下方 UCRT 检查）。`n" +
         "另：扩展支持已于 2023-10 结束，.NET 9 及以后版本不再支持该系统，建议规划迁移。")
}
else {
    Write-Result OK "操作系统：$caption（$osVersion）"
}

# --- 64 位 ----------------------------------------------------------------
if ([Environment]::Is64BitOperatingSystem) {
    Write-Result OK "处理器架构：64 位"
}
else {
    Write-Result FAIL "非 64 位操作系统" "服务端与内置 PostgreSQL 均只提供 x64 版本。"
}

# --- Universal C Runtime --------------------------------------------------
# 这是 Server 2012 上最常见的失败点：缺了它，apphost 在加载 hostfxr 前就被系统加载器
# 拦下，弹出“丢失 api-ms-win-crt-string-l1-1-0.dll”。
# 判定方式随系统版本而不同：Win10/Server 2016 起 api-ms-win-crt-*.dll 是 API Set 虚拟
# 名称，System32 里并没有对应文件，只能看 ucrtbase.dll；而 Server 2012/2012 R2 上
# KB2999226 会把这些 DLL 实打实地装进 System32，因此在老系统上要一并核对，
# 以便发现“装了一半”的情况。
$system32 = Join-Path $env:SystemRoot 'System32'
$ucrtCore = Join-Path $system32 'ucrtbase.dll'
$ucrtForwarders = @(
    'api-ms-win-crt-runtime-l1-1-0.dll',
    'api-ms-win-crt-string-l1-1-0.dll',
    'api-ms-win-crt-heap-l1-1-0.dll',
    'api-ms-win-crt-stdio-l1-1-0.dll',
    'api-ms-win-crt-math-l1-1-0.dll',
    'api-ms-win-crt-locale-l1-1-0.dll',
    'api-ms-win-crt-convert-l1-1-0.dll',
    'api-ms-win-crt-time-l1-1-0.dll',
    'api-ms-win-crt-filesystem-l1-1-0.dll'
)

if (-not (Test-Path $ucrtCore)) {
    Write-Result FAIL "缺少 Universal C Runtime（未找到 ucrtbase.dll）" (Get-UcrtGuidance $osVersion)
}
elseif ($osVersion -lt [Version]'10.0') {
    $missingUcrt = @($ucrtForwarders | Where-Object { -not (Test-Path (Join-Path $system32 $_)) })
    if ($missingUcrt.Count -eq 0) {
        Write-Result OK "Universal C Runtime：已安装（KB2999226 或 VC++ 运行库）"
    }
    else {
        Write-Result FAIL ("Universal C Runtime 安装不完整（缺 {0} 个文件）" -f $missingUcrt.Count) `
            (($missingUcrt -join "`n") + "`n`n" + (Get-UcrtGuidance $osVersion))
    }
}
else {
    Write-Result OK "Universal C Runtime：已随系统安装"
}

# --- VC++ 运行库（PostgreSQL 依赖）----------------------------------------
# 内置的 PostgreSQL 是用 MSVC 编译的：postgres.exe 及其若干 DLL 直接导入
# vcruntime140.dll / vcruntime140_1.dll / msvcp140.dll。这三个任何 Windows 版本都不自带，
# 也不在 pgsqlin 里，必须由 VC++ 2015-2022 运行库提供。
# 缺它不影响安装程序启动（.NET 自带运行时不依赖 MSVC CRT），
# 但装完后 PostgreSQL 服务会起不来，报的是另一个 DLL 缺失，很容易误判成第二个 bug。
$vcRuntimeFiles = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')
$missingVc = @($vcRuntimeFiles | Where-Object { -not (Test-Path (Join-Path $system32 $_)) })

if ($missingVc.Count -eq 0) {
    Write-Result OK "VC++ 2015-2022 运行库：已安装"
}
else {
    # 发行包里 prerequisites\vc_redist.x64.exe 就在本脚本旁边。缺这个运行库的机器
    # 多半也没有外网，只给一个下载网址等于没给——本地有就先报本地那一份。
    $bundledVc = Join-Path $PSScriptRoot 'prerequisites\vc_redist.x64.exe'
    $howToInstall = if (Test-Path -LiteralPath $bundledVc) {
        "安装：本目录下已自带，直接运行 $bundledVc`n" +
        "      （静默安装：`"$bundledVc`" /install /passive /norestart）`n" +
        "      服务端安装程序也会检测到缺失并提示就地安装，不需要联网。"
    } else {
        "安装：https://aka.ms/vs/17/release/vc_redist.x64.exe"
    }
    Write-Result FAIL ("缺少 VC++ 2015-2022 运行库（{0} 个文件）" -f $missingVc.Count) `
        (($missingVc -join "`n") + "`n`n" +
         "内置 PostgreSQL 依赖这些 DLL，缺失会导致数据库服务无法启动。`n" +
         $howToInstall + "`n" +
         "注意：在缺 UCRT 的系统上这个安装包装不上东西，`n" +
         "      要先打完 KB2999226 补丁并重启，再重新运行一次 vc_redist。")
}

# --- UCRT 相关补丁清单（仅老系统，便于定位补丁链断在哪一环）------------
if ($osVersion -lt [Version]'10.0') {
    $wanted = if ($osVersion -ge [Version]'6.3') {
        @('KB2919442', 'KB2919355', 'KB2999226')
    } else {
        @('KB2919442', 'KB2999226')
    }
    $installed = @(Get-HotFix -ErrorAction SilentlyContinue | Select-Object -ExpandProperty HotFixID)
    # 用 @() 包住：foreach 只产出一个元素时结果是字符串，后面的 += 会变成字符串拼接。
    $inventory = @(foreach ($kb in $wanted) {
        "{0} {1}" -f $kb, $(if ($installed -contains $kb) { '已安装' } else { '未检测到' })
    })
    # Get-HotFix 读的是 Win32_QuickFixEngineering，只覆盖 MSI/QFE 式更新，
    # 以 CBS 组件方式安装的包（KB2919355 就是典型）即使装了也不会出现在里面。
    # 因此这里只作信息展示，不参与通过/失败判定；要确认得用 DISM 复核。
    $inventory += "注：Get-HotFix 查不到以 CBS 方式安装的包，KB2919355 尤其常见漏报。"
    $inventory += "    复核：dism /online /get-packages | Select-String 2919355"
    Write-Result OK "UCRT 补丁链现状" ($inventory -join "`n")
}

# --- 管理员权限 -----------------------------------------------------------
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Write-Result OK "当前会话具备管理员权限"
}
else {
    Write-Result WARN "当前会话不是管理员" "安装程序需要以管理员身份运行（右键 → 以管理员身份运行）。"
}

# --- 磁盘空间 -------------------------------------------------------------
$systemDrive = (Get-WmiObject Win32_LogicalDisk -Filter "DeviceID='$($env:SystemDrive)'")
$freeGb = [math]::Round($systemDrive.FreeSpace / 1GB, 1)
if ($freeGb -lt 5) {
    Write-Result WARN "$($env:SystemDrive) 剩余空间 $freeGb GB" "服务端 + 内置 PostgreSQL 建议至少预留 5 GB。"
}
else {
    Write-Result OK "$($env:SystemDrive) 剩余空间 $freeGb GB"
}

# --- 结论 -----------------------------------------------------------------
Write-Host ("=" * 60)
if ($script:Failures -gt 0) {
    Write-Host "自检未通过：$($script:Failures) 项阻断、$($script:Warnings) 项警告。请先处理阻断项。" -ForegroundColor Red
    exit 1
}
if ($script:Warnings -gt 0) {
    Write-Host "自检通过，但有 $($script:Warnings) 项警告，请确认后再继续安装。" -ForegroundColor Yellow
    exit 0
}
Write-Host "自检全部通过，可以运行 BackupMonitor.Server.Setup.exe。" -ForegroundColor Green
exit 0
