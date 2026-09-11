#Requires -Version 3.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ServerUrl,
    [Parameter(Mandatory = $true)] [string] $RegistrationToken,
    [Parameter(Mandatory = $true)] [string] $ServerSigningPublicKey,
    [string] $DisplayName = $env:COMPUTERNAME,
    [string] $InstallDir = "$env:ProgramFiles\BackupMonitor\Agent",
    [string] $DataDirectory = "$env:ProgramData\BackupMonitor\Agent",
    [string] $ServiceName = "BackupMonitor Agent",
    [string] $UpdaterDir = "$env:ProgramFiles\BackupMonitor\Updater",
    [switch] $Silent
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installing BackupMonitor Agent requires an elevated PowerShell session.'
}

$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null

# 递归拷贝：自包含发布除了根目录的运行时 DLL，还带本地化资源子目录（cs/de/ja/zh-Hans...）。
# 只拷根目录文件会把它们全部丢掉。
Get-ChildItem -LiteralPath $packageRoot -Force | Where-Object {
    $_.Name -notin @('install-agent.ps1', 'uninstall-agent.ps1', 'updater')
} | Copy-Item -Destination $InstallDir -Recurse -Force

# 升级执行器装到安装目录的**同级**目录（R20）。装在安装目录里的话，
# 远程升级那一刻它自己的文件也被锁着，换文件就做不成——
# 而换不成的表现恰恰是这次整改要消灭的那种「报成功但什么都没换」。
$updaterPayload = Join-Path $packageRoot 'updater'
if (Test-Path -LiteralPath $updaterPayload) {
    New-Item -ItemType Directory -Path $UpdaterDir -Force | Out-Null
    Get-ChildItem -LiteralPath $updaterPayload -Force |
        Copy-Item -Destination $UpdaterDir -Recurse -Force
}

$settingsPath = Join-Path $InstallDir 'appsettings.json'
$settings = if (Test-Path -LiteralPath $settingsPath) {
    Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
} else {
    [pscustomobject]@{ Agent = [pscustomobject]@{} }
}
if ($null -eq $settings.Agent) {
    $settings | Add-Member -MemberType NoteProperty -Name Agent -Value ([pscustomobject]@{})
}
$settings.Agent.ServerUrl = $ServerUrl.TrimEnd('/')
$settings.Agent.RegistrationToken = ''
$settings.Agent.ServerSigningPublicKey = $ServerSigningPublicKey.Trim()
$settings.Agent.AllowUnsignedCommands = $false
$settings.Agent.DisplayName = $DisplayName
$settings.Agent.DataDirectory = $DataDirectory
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

# 注册令牌只在首次注册期间存在于受 ACL 保护的 ProgramData 文件中，注册成功后由 Agent 删除。
$registrationTokenPath = Join-Path $DataDirectory 'registration-token.txt'
Set-Content -LiteralPath $registrationTokenPath -Value $RegistrationToken -Encoding UTF8 -NoNewline
& icacls.exe $registrationTokenPath /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to protect registration token file: $registrationTokenPath"
}

$exePath = Join-Path $InstallDir 'BackupMonitor.Agent.exe'
$trayPath = Join-Path $InstallDir 'BackupMonitor.Agent.Tray.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw 'Release package is missing BackupMonitor.Agent.exe.' }
if (-not (Test-Path -LiteralPath $trayPath)) { throw 'Release package is missing BackupMonitor.Agent.Tray.exe.' }

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    & sc.exe delete $ServiceName | Out-Null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 250
    }
}

New-Service -Name $ServiceName `
    -BinaryPathName ('"{0}"' -f $exePath) `
    -DisplayName $ServiceName `
    -Description 'BackupMonitor Agent data collection service' `
    -StartupType Automatic | Out-Null

# SCM restarts unexpected service failures with backoff. A normal Stop-Service is not a failure.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null
& sc.exe config $ServiceName start= delayed-auto | Out-Null

# The service owns collection/upload. The tray is only the per-user status UI.
# HKCU Run starts the tray after login; closing the tray does not stop the service.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
$safeServiceName = $ServiceName.Replace('"', '')
$safeServerUrl = $ServerUrl.TrimEnd('/').Replace('"', '')
$safeDataDirectory = $DataDirectory.Replace('"', '')
$trayArguments = "--service-name `"$safeServiceName`" --server-url `"$safeServerUrl`" --data-directory `"$safeDataDirectory`""
$trayCommand = "`"$trayPath`" $trayArguments"
Set-ItemProperty -Path $runKey -Name 'BackupMonitor.Agent.Tray' -Value $trayCommand

Start-Service -Name $ServiceName
Start-Process -FilePath $trayPath -ArgumentList $trayArguments

if (-not $Silent) {
    Write-Host 'BackupMonitor Agent installed and started.'
    Write-Host "Server: $ServerUrl"
    Write-Host "Data directory: $DataDirectory"
    Write-Host 'Approve the pending registration request in the server console.'
}
