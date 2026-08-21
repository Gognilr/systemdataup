<#
.SYNOPSIS
    在 Windows Server 2012 / 2012 R2 上按正确顺序安装 UCRT 补丁链（KB2999226 及其前置）。

.DESCRIPTION
    这是"一键"处理，但不联网下载：Microsoft Update Catalog 的下载链接是动态生成的、
    没有稳定直链，脚本硬编码 URL 迟早会失效并把人引向不明来源。所以流程是
    你先把 MSU 放进一个目录，脚本负责识别、排序、静默安装、判读退出码和重启节奏——
    出错最多的恰恰是顺序和重启，而不是下载本身。

    首选方案其实是直接跑完 Windows Update 全量更新，它会自动处理整条依赖。
    本脚本面向不能连外网、只能离线送补丁的机器。

.PARAMETER PackageDirectory
    存放 .msu 文件的目录。脚本按文件名识别 KB 编号，与摆放顺序无关。

.PARAMETER WhatIf
    只列出将要执行的安装顺序，不实际安装。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-UcrtPrerequisites.ps1 -PackageDirectory D:\msu
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'

# 面向运维操作员，失败时只给可读的处置说明，不吐 PowerShell 堆栈。
function Fail {
    param([string]$Message)
    Write-Host ''
    foreach ($line in $Message -split "`n") { Write-Host $line -ForegroundColor Red }
    exit 1
}

# --- 前置校验 -------------------------------------------------------------
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail '请以管理员身份运行本脚本。'
}

if (-not (Test-Path -LiteralPath $PackageDirectory)) {
    Fail "目录不存在：$PackageDirectory"
}

$osVersion = [Version](Get-WmiObject Win32_OperatingSystem).Version
if ($osVersion -ge [Version]'10.0') {
    Write-Host ' 本系统自带 UCRT，无需安装这些补丁。' -ForegroundColor Green
    exit 0
}
if ($osVersion -lt [Version]'6.2') {
    Fail "不支持的系统版本：$osVersion"
}

$isR2 = $osVersion -ge [Version]'6.3'

# 安装顺序即依赖顺序，倒过来装会得到"此更新不适用于你的计算机"。
# KB2919355 只有 2012 R2 需要（它是 Update 1，2012 没有对应包）。
# 2012 R2 的 Update 1 在 Update Catalog 上不是一个包，而是一组，且组内也有固定顺序。
# 其中 KB2919442 与 KB2919355 是硬前置（缺了 KB2999226 直接报"不适用"），
# 其余几个是微软文档给出的完整顺序，装了更稳；标 Optional 的在目录里找不到就跳过，
# 不当作错误——很多机器已经通过 Windows Update 装过其中一部分。
$plan = if ($isR2) {
    @(
        @{ Kb = 'KB2919442'; Name = '服务堆栈更新';        Optional = $false }
        @{ Kb = 'KB2919355'; Name = 'Update 1 主包';       Optional = $false }
        @{ Kb = 'KB2932046'; Name = 'Update 1 组件';       Optional = $true  }
        @{ Kb = 'KB2934018'; Name = 'Update 1 组件';       Optional = $true  }
        @{ Kb = 'KB2937592'; Name = 'Update 1 组件';       Optional = $true  }
        @{ Kb = 'KB2938439'; Name = 'Update 1 组件';       Optional = $true  }
        @{ Kb = 'KB2959977'; Name = 'Update 1 组件';       Optional = $true  }
        @{ Kb = 'KB2999226'; Name = 'UCRT';                Optional = $false }
    )
} else {
    @(
        @{ Kb = 'KB2919442'; Name = '服务堆栈更新';        Optional = $false }
        @{ Kb = 'KB2999226'; Name = 'UCRT';                Optional = $false }
    )
}

$osLabel = if ($isR2) { 'Windows Server 2012 R2' } else { 'Windows Server 2012' }
Write-Host "目标系统：$osLabel（$osVersion）" -ForegroundColor Cyan
Write-Host ("=" * 60)

# --- 匹配 MSU 文件 --------------------------------------------------------
$msuFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.msu' -File)
if ($msuFiles.Count -eq 0) {
    Fail ("$PackageDirectory 下没有 .msu 文件。请先从 Microsoft Update Catalog 下载：`n" +
          "  https://www.catalog.update.microsoft.com/Search.aspx?q=KB2999226")
}

# 包名必须与系统匹配：2012 R2 是 Windows8.1-*，2012 是 Windows8-RT-*。
# 装错平台的包同样报"此更新不适用"，这里提前拦下，省得到 wusa 才发现。
$expectedPrefix = if ($isR2) { 'Windows8.1-' } else { 'Windows8-RT-' }
$wrongPlatform = @($msuFiles | Where-Object { $_.Name -notlike "$expectedPrefix*" })
if ($wrongPlatform.Count -gt 0) {
    Write-Host '以下文件与本系统不匹配，将被跳过：' -ForegroundColor Yellow
    $wrongPlatform | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Yellow }
    Write-Host "  本系统需要 $expectedPrefix 开头的包。" -ForegroundColor Yellow
    Write-Host ''
}

$missing = @()
foreach ($step in $plan) {
    $match = $msuFiles |
        Where-Object { $_.Name -like "$expectedPrefix*" -and $_.Name -match $step.Kb } |
        Select-Object -First 1
    $step.File = $match
    if (-not $match -and -not $step.Optional) { $missing += $step.Kb }
}


Write-Host '安装计划：'
foreach ($step in $plan) {
    $state = if ($step.File) { $step.File.Name }
             elseif ($step.Optional) { '未提供（可选，跳过）' }
             else { '【缺少文件】' }
    Write-Host ("  {0} - {1} - {2}" -f $step.Kb, $step.Name, $state)
}
Write-Host ''

if ($missing.Count -gt 0) {
    Fail ("缺少以下补丁的 MSU 文件：$($missing -join '、')`n" +
          "从 https://www.catalog.update.microsoft.com 按 KB 号搜索下载，" +
          "选 $expectedPrefix 开头、x64 的包。")
}

# 目录里没有的可选包直接从计划中剔除，免得后面循环还要处理空文件。
$plan = @($plan | Where-Object { $_.File })

# --- 逐个安装 -------------------------------------------------------------
# wusa 的退出码：0 成功；3010 成功但需重启；2359302 已安装；
# -2145124329 (0x80240017) 不适用——通常就是前置缺失或包选错平台。
$rebootRequired = $false

foreach ($step in $plan) {
    $file = $step.File
    Write-Host "正在安装 $($step.Kb)（$($step.Name)）：$($file.Name)" -ForegroundColor Cyan

    if (-not $PSCmdlet.ShouldProcess($file.Name, "wusa /quiet /norestart")) { continue }

    $process = Start-Process -FilePath 'wusa.exe' `
        -ArgumentList @("`"$($file.FullName)`"", '/quiet', '/norestart') `
        -Wait -PassThru
    $code = $process.ExitCode

    switch ($code) {
        0 {
            Write-Host "  完成。" -ForegroundColor Green
        }
        3010 {
            Write-Host "  完成，需要重启。" -ForegroundColor Green
            $rebootRequired = $true
        }
        2359302 {
            Write-Host "  已安装，跳过。" -ForegroundColor Green
        }
        -2145124329 {
            Fail ("$($step.Kb) 报告'不适用于此计算机'（0x80240017）。`n" +
                  "常见原因：前一个补丁尚未生效（需先重启）、或 MSU 包与系统平台不符。`n" +
                  "请重启后重新运行本脚本。")
        }
        default {
            Fail ("$($step.Kb) 安装失败，wusa 退出码 $code。`n" +
                  "详细信息见事件查看器：应用程序和服务日志 → Microsoft → Windows → WindowsUpdateClient。")
        }
    }

    # 前置补丁没生效就装下一个，必定报"不适用"。这里遇到需重启就停，
    # 让操作员重启后再跑一次——脚本会自动跳过已安装项，从断点继续。
    if ($rebootRequired -and $step -ne $plan[-1]) {
        Write-Host ''
        Write-Host "需要重启后才能继续安装后续补丁。" -ForegroundColor Yellow
        Write-Host "请重启，然后重新运行本脚本（已装的会自动跳过）。" -ForegroundColor Yellow
        exit 3010
    }
}

# --- 结果校验 -------------------------------------------------------------
Write-Host ''
Write-Host ("=" * 60)
if ($rebootRequired) {
    Write-Host '全部补丁已安装，请重启后运行 Check-Prerequisites.ps1 复核。' -ForegroundColor Yellow
    exit 3010
}

$ucrtOk = Test-Path (Join-Path $env:SystemRoot 'System32\ucrtbase.dll')
if ($ucrtOk) {
    Write-Host 'UCRT 已就位，可以运行 BackupMonitor.Server.Setup.exe。' -ForegroundColor Green
    exit 0
}

Write-Host 'UCRT 仍未就位，请重启后运行 Check-Prerequisites.ps1 查看详情。' -ForegroundColor Yellow
exit 1
