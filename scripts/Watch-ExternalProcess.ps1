<#
.SYNOPSIS
    看住一个「不是 Windows 服务」的后台进程：没了就按它原来的样子拉起来。

.DESCRIPTION
    有些第三方业务进程是裸跑的——不是服务，靠某个账户登录后由 bat 拉起来。
    典型代表是致远 A6 的 OfficeTrans 文件转换服务：
    java.exe 挂在 Administrator 会话里，`-Xms8G`，没有服务注册。

    这种跑法有两个后果，而第二个比第一个严重得多：
      1. 它崩了不会自己回来；
      2. **机器一重启、或者那个账户一注销，它就没了，而且永远不会自己回来**——
         直到有人想起来登进去手点一下。

    正规做法是把它注册成 Windows 服务（NSSM / WinSW），那样服务恢复选项
    和开机自启都是系统白给的。但现场经常动不了第三方的安装方式，
    这个脚本就是在动不了的前提下，用 Windows 任务计划程序在外面补上这一层。

    ── 边界 ──────────────────────────────────────────────
    它只解决「进程没了」。**进程还在但已经卡死（JVM OOM、Full GC 停不下来）
    它一概不管**，因为「卡死」没有本地可靠判据，而按一个不可靠的判据去杀掉
    并重启一个生产进程，比不管更危险——探测误判一次，正在转的文件就断一次。
    「卡死」那一半交给服务端的业务探测去报警，由人来决定动不动手。

.PARAMETER Learn
    学习模式。趁目标进程活着的时候跑一次，把它的完整命令行和工作目录记到状态文件里，
    以后就照这个拉。比人手抄命令行靠谱——抄漏一个 -D 参数，拉起来的是另一个东西，
    而它还会装作正常运行。

.PARAMETER Install
    把自己注册成计划任务（开机 + 每 N 分钟）。需要管理员权限。

.EXAMPLE
    # 第一步：趁它活着，学一次
    .\Watch-ExternalProcess.ps1 -Learn -MatchCommandLine '*OfficeTrans*'

    # 第二步：确认学到的东西没错之后，装成计划任务
    .\Watch-ExternalProcess.ps1 -Install -MatchCommandLine '*OfficeTrans*' -VerifyPort 1097,1098
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Learn', Mandatory = $true)]
    [switch] $Learn,

    [Parameter(ParameterSetName = 'Install', Mandatory = $true)]
    [switch] $Install,

    # 怎么认出目标进程。按命令行关键字匹配，而不是按进程名——
    # 这台机器上 java.exe 有好几个（A6 主应用、搜索服务、转换服务），
    # 只看进程名会认错，而认错的后果是把别人的进程当成自己的。
    [string] $MatchCommandLine = '*OfficeTrans*',

    [string] $ProcessName = 'java.exe',

    # 拉起之后要确认这些端口真的回来了。只看「进程起来了」不够：
    # 8G 堆的 JVM 可能起到一半就因为内存不足退出，而那几秒里进程是存在的。
    [int[]] $VerifyPort = @(),

    [int] $VerifyTimeoutSeconds = 180,

    # 两次重启之间至少隔这么久。防的是「它根本起不来」那种情况——
    # 没有这道闸，看门狗会每分钟造一个起不来的 8G java 进程，一天一千多次。
    [int] $MinRestartIntervalMinutes = 10,

    # 一天最多重启几次。超了就只记日志不动手：
    # 一天要拉起十次的东西，问题不在「没人拉它」，硬拉只会盖住真正的原因。
    [int] $MaxRestartsPerDay = 6,

    [Parameter(ParameterSetName = 'Install')]
    [int] $IntervalMinutes = 5,

    [string] $StateFile = (Join-Path $PSScriptRoot 'watch-external-process.state.json'),
    [string] $LogFile = (Join-Path $PSScriptRoot 'watch-external-process.log'),

    # 学习模式下推断错了可以手工指定
    [string] $WorkingDirectory,

    # 更推荐的拉起方式：如果第三方自带启动脚本，用它们自己的，
    # 比我们复刻一条命令行安全——它们的脚本里可能还做了别的事。
    [string] $StartCommand,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$TaskName = 'BackupMonitor-WatchExternalProcess'

function Write-Log {
    param([string] $Message, [ValidateSet('Info', 'Warn', 'Error')][string] $Level = 'Info')

    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level.ToUpperInvariant(), $Message
    Write-Host $line

    # 日志文件是给事后查的，不能因为磁盘满或者权限问题把看门狗本身弄崩。
    try { Add-Content -LiteralPath $LogFile -Value $line -Encoding utf8 } catch { }

    # 重启这种事要在事件查看器里留一笔：那才是运维会去翻的地方，
    # 而一个只写在自己目录里的日志文件，出事时没人想得起来。
    if ($Level -ne 'Info') {
        try {
            if (-not [Diagnostics.EventLog]::SourceExists($TaskName)) {
                [Diagnostics.EventLog]::CreateEventSource($TaskName, 'Application')
            }
            $type = if ($Level -eq 'Error') { 'Error' } else { 'Warning' }
            Write-EventLog -LogName Application -Source $TaskName -EntryType $type -EventId 9001 -Message $Message
        } catch { }
    }
}

function Find-Target {
    # Win32_Process 才有 CommandLine，Get-Process 没有——而命令行正是唯一能
    # 把这几个 java.exe 区分开的东西。
    Get-CimInstance Win32_Process -Filter ("Name='{0}'" -f $ProcessName) -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like $MatchCommandLine } |
        Select-Object -First 1
}

function Resolve-WorkingDirectory {
    param([string] $ExecutablePath)

    # Win32_Process 不给工作目录（那在 PEB 里，PowerShell 够不着）。
    # 但这类 Java 进程的命令行里几乎都带相对路径（-Dxxx=conf/yyy.properties），
    # 工作目录错了它起来就读不到配置。所以从 exe 路径往上找第一个含 conf\ 的目录，
    # 这对 <产品根>\jdk\bin\java.exe 这种布局是准的。
    $dir = Split-Path -Parent $ExecutablePath
    while ($dir -and (Split-Path -Parent $dir)) {
        if (Test-Path -LiteralPath (Join-Path $dir 'conf')) { return $dir }
        $dir = Split-Path -Parent $dir
    }
    return (Split-Path -Parent $ExecutablePath)
}

function Split-CommandLine {
    param([string] $CommandLine, [string] $ExecutablePath)

    # 不去解析命令行的第一个 token（路径里可能有空格，带不带引号还不一定），
    # 直接拿 ExecutablePath 当 exe，把它从命令行头部切掉，剩下的就是参数。
    $trimmed = $CommandLine.Trim()
    foreach ($prefix in @(('"{0}"' -f $ExecutablePath), $ExecutablePath)) {
        if ($trimmed.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $trimmed.Substring($prefix.Length).Trim()
        }
    }

    # exe 路径和命令行对不上（少见：命令行里写的是相对路径或短路径）。
    # 这种情况下不猜，让学习模式失败，由人手工指定 -StartCommand。
    throw "命令行和可执行文件路径对不上，无法安全地切分参数。请改用 -StartCommand 指定启动方式。`n  命令行: $CommandLine`n  可执行: $ExecutablePath"
}

function Read-State {
    if (-not (Test-Path -LiteralPath $StateFile)) { return $null }
    try { return Get-Content -LiteralPath $StateFile -Raw -Encoding utf8 | ConvertFrom-Json }
    catch { Write-Log "状态文件读不出来，按没有处理：$($_.Exception.Message)" Warn; return $null }
}

function Write-State {
    param($State)
    $State | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $StateFile -Encoding utf8
}

function Wait-Port {
    param([int[]] $Ports, [int] $TimeoutSeconds)

    if (-not $Ports -or $Ports.Count -eq 0) { return $true }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $missing = @($Ports | Where-Object {
            -not (Get-NetTCPConnection -LocalPort $_ -State Listen -ErrorAction SilentlyContinue)
        })
        if ($missing.Count -eq 0) { return $true }
        Start-Sleep -Seconds 5
    }
    return $false
}

# ── 学习模式 ────────────────────────────────────────────────
if ($Learn) {
    $proc = Find-Target
    if (-not $proc) {
        throw "没找到匹配的进程（$ProcessName，命令行含 $MatchCommandLine）。学习模式必须在它活着的时候跑。"
    }

    $cwd = if ($WorkingDirectory) { $WorkingDirectory } else { Resolve-WorkingDirectory $proc.ExecutablePath }
    $state = [ordered]@{
        processName      = $ProcessName
        matchCommandLine = $MatchCommandLine
        executablePath   = $proc.ExecutablePath
        arguments        = Split-CommandLine $proc.CommandLine $proc.ExecutablePath
        workingDirectory = $cwd
        learnedAt        = (Get-Date).ToString('o')
        learnedFromPid   = $proc.ProcessId
        restarts         = @()
    }
    Write-State $state

    Write-Host ''
    Write-Host '学到的启动方式如下，装计划任务之前请先核对一遍：' -ForegroundColor Cyan
    Write-Host "  可执行文件 : $($state.executablePath)"
    Write-Host "  参数       : $($state.arguments)"
    Write-Host "  工作目录   : $($state.workingDirectory)  <- 这一项是推断的，尤其要确认"
    Write-Host "  状态文件   : $StateFile"
    Write-Host ''
    Write-Host '工作目录推断错的话，重新跑一次并加上 -WorkingDirectory "<正确路径>"。' -ForegroundColor Yellow
    return
}

# ── 安装成计划任务 ──────────────────────────────────────────
if ($Install) {
    if (-not (Read-State)) {
        throw "还没有状态文件。请先在目标进程活着的时候跑一次 -Learn。"
    }

    # 不能叫 $args：那是 PowerShell 的自动变量，占用它会在别处引发很难查的怪事。
    $taskArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-MatchCommandLine', ('"{0}"' -f $MatchCommandLine),
        '-ProcessName', ('"{0}"' -f $ProcessName),
        '-MinRestartIntervalMinutes', $MinRestartIntervalMinutes,
        '-MaxRestartsPerDay', $MaxRestartsPerDay
    )
    if ($VerifyPort.Count -gt 0) { $taskArgs += @('-VerifyPort', ($VerifyPort -join ',')) }

    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($taskArgs -join ' ')

    # 两个触发器缺一不可：
    #   开机那一下解决「机器重启之后它再也不回来」，这是现在最严重的那个问题；
    #   周期那一下解决「跑着跑着崩了」。
    $triggers = @(
        (New-ScheduledTaskTrigger -AtStartup),
        # 重复时长要显式给满：只写 -RepetitionInterval 不写时长，
        # 在部分 Windows 版本上注册出来的是一个「只跑一次」的触发器——
        # 而那个错误的表现是「装好了，然后什么也没发生」。
        (New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
            -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
            -RepetitionDuration (New-TimeSpan -Days 3650))
    )

    # 必须用目标进程原来那个账户跑，而且要「不管用户是否登录都运行」——
    # 后者正是让它不再依赖「有人登录着」的关键。
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew `
        -StartWhenAvailable -DontStopOnIdleEnd -ExecutionTimeLimit (New-TimeSpan -Hours 1)

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $triggers `
        -Principal $principal -Settings $settings -Force | Out-Null

    Write-Log "计划任务已注册：$TaskName（开机 + 每 $IntervalMinutes 分钟）"
    Write-Host ''
    Write-Host "默认以 SYSTEM 运行。如果目标进程依赖某个特定账户（映射盘、用户级环境变量），" -ForegroundColor Yellow
    Write-Host "到「任务计划程序」里打开 $TaskName → 常规 → 更改用户或组，改成那个账户，" -ForegroundColor Yellow
    Write-Host "并勾选「不管用户是否登录都运行」。改账户时 Windows 会自己弹框要密码——" -ForegroundColor Yellow
    Write-Host "走那个框，不要把密码写进任何脚本或命令行。" -ForegroundColor Yellow
    return
}

# ── 巡检 ────────────────────────────────────────────────────
$state = Read-State
if (-not $state) {
    Write-Log "没有状态文件（$StateFile），不知道该怎么拉起它。请先跑 -Learn。" Error
    exit 1
}

if (Find-Target) {
    # 正常情况下每 5 分钟走的就是这一条，所以它只写文件、不写事件日志。
    Write-Log "进程在，无需处理。"
    exit 0
}

$now = Get-Date
$restarts = @($state.restarts | ForEach-Object { [datetime]::Parse($_) })

$last = $restarts | Sort-Object -Descending | Select-Object -First 1
if ($last -and ($now - $last).TotalMinutes -lt $MinRestartIntervalMinutes) {
    Write-Log ("进程不在，但距离上次重启只有 {0:N1} 分钟（下限 {1} 分钟），本次不动手。" -f ($now - $last).TotalMinutes, $MinRestartIntervalMinutes) Warn
    exit 0
}

$today = @($restarts | Where-Object { $_.Date -eq $now.Date })
if ($today.Count -ge $MaxRestartsPerDay) {
    Write-Log ("进程不在，但今天已经重启过 {0} 次（上限 {1}），本次不动手——一天要拉起这么多次，问题不在没人拉它。" -f $today.Count, $MaxRestartsPerDay) Error
    exit 0
}

if ($DryRun) {
    Write-Log "进程不在。（DryRun）本该执行：$($state.executablePath) $($state.arguments)  于 $($state.workingDirectory)" Warn
    exit 0
}

Write-Log "进程不在，开始拉起。" Warn
try {
    if ($StartCommand) {
        Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $StartCommand `
            -WorkingDirectory $state.workingDirectory -WindowStyle Hidden
    } else {
        Start-Process -FilePath $state.executablePath -ArgumentList $state.arguments `
            -WorkingDirectory $state.workingDirectory -WindowStyle Hidden
    }
} catch {
    Write-Log "拉起失败：$($_.Exception.Message)" Error
    exit 1
}

# 记重启这一笔要在**验证之前**：起没起来是另一回事，
# 「我们动过手」这件事本身必须留痕，否则节流的闸门就形同虚设。
$state.restarts = @($restarts + $now | Sort-Object -Descending | Select-Object -First 50 |
    ForEach-Object { $_.ToString('o') })
Write-State $state

# 只确认进程存在是不够的：8G 堆的 JVM 可能起到一半因为内存不足退出，
# 而那几秒里它确实是「存在」的。所以要等端口真的回来。
if ($VerifyPort.Count -gt 0) {
    if (Wait-Port -Ports $VerifyPort -TimeoutSeconds $VerifyTimeoutSeconds) {
        Write-Log ("已拉起，端口 {0} 已恢复监听。" -f ($VerifyPort -join ', ')) Warn
    } else {
        Write-Log ("已拉起进程，但等了 {0} 秒端口 {1} 仍未监听——它可能没起成功，请人工检查。" -f $VerifyTimeoutSeconds, ($VerifyPort -join ', ')) Error
        exit 1
    }
} else {
    Start-Sleep -Seconds 10
    if (Find-Target) { Write-Log "已拉起。" Warn }
    else { Write-Log "拉起之后进程仍然不在，请人工检查。" Error; exit 1 }
}
