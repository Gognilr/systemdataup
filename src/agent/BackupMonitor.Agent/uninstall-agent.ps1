#Requires -Version 3.0
[CmdletBinding()]
param(
    [string] $InstallDir = "$env:ProgramFiles\BackupMonitor\Agent",
    [string] $DataDirectory = "$env:ProgramData\BackupMonitor\Agent",
    [string] $ServiceName = "BackupMonitor Agent",
    [string] $UpdaterDir = "$env:ProgramFiles\BackupMonitor\Updater",
    [switch] $KeepData
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Uninstalling BackupMonitor Agent requires an elevated PowerShell session.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    & sc.exe delete $ServiceName | Out-Null
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'BackupMonitor.Agent.Tray' -ErrorAction SilentlyContinue

$trayPath = Join-Path $InstallDir 'BackupMonitor.Agent.Tray.exe'
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -eq $trayPath } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}
# 升级执行器在安装目录之外（R20），删安装目录删不到它。
if (Test-Path -LiteralPath $UpdaterDir) {
    Remove-Item -LiteralPath $UpdaterDir -Recurse -Force
}
if (-not $KeepData -and (Test-Path -LiteralPath $DataDirectory)) {
    Remove-Item -LiteralPath $DataDirectory -Recurse -Force
}

Write-Host "BackupMonitor Agent uninstalled. KeepData=$KeepData"
