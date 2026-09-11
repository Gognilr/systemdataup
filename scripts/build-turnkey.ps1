param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $PayloadCacheRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BackupMonitor\payload-cache')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src'
$stageRoot = Join-Path $repoRoot 'artifacts\turnkey-stage'
$distRoot = Join-Path $repoRoot 'dist'
$setupPayloadRoot = Join-Path $srcRoot 'server\BackupMonitor.Server.Setup\payload'
$payloadLockPath = Join-Path $PSScriptRoot 'payload-lock.json'

function Assert-WorkspaceTarget {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolvedRepo = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
    $resolvedTarget = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    if (-not $resolvedTarget.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside workspace: $Path"
    }
}

function Reset-GeneratedDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-WorkspaceTarget $Path
    if (Test-Path -LiteralPath $Path) {
        # Keep the generated directory root. Windows may hold a handle to the
        # root while still allowing its generated children to be replaced.
        Get-ChildItem -LiteralPath $Path -Force | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    } else {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    & 'C:\Program Files\dotnet\dotnet.exe' @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Invoke-ResilientCopy {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    # 与 Invoke-VerifiedCompress 里那段重试同一个成因，只是发生在拷贝上：
    # 杀软 / Windows Search 索引器会去扫刚发布出来的大文件（单文件发布的
    # Setup.exe 有 160MB+），扫描期间它被内存映射，Copy-Item 直接抛
    # 「The requested operation cannot be performed on a file with a user-mapped section open」。
    # 这不是构建逻辑的问题，等一会儿就好；但次数是有界的，用尽照样抛错，
    # 绝不退化成「静默产出缺文件的包」——那正是 $payloadRequired 清单要防的事。
    $attempts = 5
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            Copy-Item -LiteralPath $Path -Destination $Destination -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -eq $attempts) {
                throw "Copy-Item failed after ${attempts} attempts (${Path} -> ${Destination}): $($_.Exception.Message)"
            }
            Write-Host "Copy attempt ${attempt}/${attempts} failed (${Destination}): $($_.Exception.Message)"
            Start-Sleep -Seconds (10 * $attempt)
        }
    }
}

function Invoke-VerifiedCompress {
    param(
        [Parameter(Mandatory = $true)][string[]] $Path,
        [Parameter(Mandatory = $true)][string] $DestinationPath,
        [Parameter(Mandatory = $true)][string[]] $RequiredEntry
    )

    # Compress-Archive 在源文件被占用（杀软扫描、进程还没退干净）时只写一条
    # 非终止错误 —— 脚本顶上的 $ErrorActionPreference 是模块外的，管不到它 ——
    # 然后照常返回。这样打出来的是一个「下载客户端」404 的安装包，而构建退出码是 0。
    # 这里做两件事：把它的错误升级成终止错误，压完之后再把包真的打开点名核对。
    # 重试是针对一个反复观测到的现象：杀毒软件在扫刚发布/刚解压出来的几千个文件，
    # 期间随机某个文件短时间不可读。这不是构建逻辑的问题，等一会儿就好。
    # 但重试次数是有界的——次数用尽照样抛错，绝不退化成「静默产出坏包」。
    # 4 次 / 约 60 秒对 server-payload.zip（220MB+，且就写在仓库目录里）不够：
    # 在开着 Windows Search 索引的机器上实测连续 5 次构建都耗尽重试后抛错。
    # 放宽到 6 次 / 约 225 秒。上界仍然存在——用尽照样抛错，
    # 「绝不退化成静默产出坏包」这条不变。
    $attempts = 6
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            Compress-Archive -Path $Path -DestinationPath $DestinationPath -CompressionLevel Optimal -Force -ErrorAction Stop
            break
        } catch {
            if ($attempt -eq $attempts) {
                throw "Compress-Archive failed after ${attempts} attempts for ${DestinationPath}: $($_.Exception.Message)"
            }
            Write-Host "Compress-Archive attempt ${attempt}/${attempts} failed (${DestinationPath}): $($_.Exception.Message)"
            if (Test-Path -LiteralPath $DestinationPath) {
                Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue
            }
            Start-Sleep -Seconds (15 * $attempt)
        }
    }

    if (-not (Test-Path -LiteralPath $DestinationPath)) {
        throw "Compress-Archive did not produce ${DestinationPath}."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($DestinationPath)
    try {
        # zip 条目分隔符两种都可能出现，统一成反斜杠再比。
        # 反斜杠用 [char]92 拼，免得转义在编辑链路上被吃掉。
        $sep = [string][char]92
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('/', $sep) })
        $missing = @($RequiredEntry | Where-Object { $entries -notcontains $_.Replace('/', $sep) })
    } finally {
        $archive.Dispose()
    }
    if ($missing.Count -gt 0) {
        throw "Archive ${DestinationPath} is missing required entries: $($missing -join ', ')"
    }
    Write-Host "Verified archive ${DestinationPath} ($($entries.Count) entries)"
}

if (-not (Test-Path -LiteralPath $payloadLockPath)) {
    throw "Payload lock file is missing: $payloadLockPath"
}
$payloadLock = Get-Content -LiteralPath $payloadLockPath -Raw | ConvertFrom-Json
$postgresLock = $payloadLock.postgresql
foreach ($property in @('version', 'archiveName', 'url', 'sha256')) {
    if ([string]::IsNullOrWhiteSpace([string]$postgresLock.$property)) {
        throw "PostgreSQL payload lock is missing '$property'."
    }
}
if ($postgresLock.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'PostgreSQL payload lock sha256 must be exactly 64 hexadecimal characters.'
}

New-Item -ItemType Directory -Path $PayloadCacheRoot -Force | Out-Null
$postgresArchive = Join-Path $PayloadCacheRoot $postgresLock.archiveName
if (-not (Test-Path -LiteralPath $postgresArchive)) {
    Write-Host "Downloading locked PostgreSQL payload: $postgresLock.url"
    Invoke-WebRequest -Uri $postgresLock.url -OutFile $postgresArchive -UseBasicParsing
}
$actualArchiveHash = (Get-FileHash -LiteralPath $postgresArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualArchiveHash -ne $postgresLock.sha256.ToLowerInvariant()) {
    throw "PostgreSQL payload SHA-256 mismatch. Expected $($postgresLock.sha256), got ${actualArchiveHash}: $postgresArchive"
}

# ---- VC++ 2015-2022 x64 运行库 -------------------------------------------
# 一个包两个用途：
#   1) 服务端内置的 PostgreSQL 是 MSVC 编译的，直接导入 vcruntime140.dll /
#      vcruntime140_1.dll / msvcp140.dll，这三个任何 Windows 版本都不自带；
#   2) 客户端下载页要给 Server 2012 / 2012 R2 一个可下载的运行库入口。
# 这里改成与 PostgreSQL 同一套「URL + SHA-256 锁定」，并保留 Authenticode 复核。
# aka.ms/vs/17/release 是「当前版本」指针，会随微软发布而变：哈希失配多半不是投毒
# 而是微软发了新版，所以失败信息里要写清正确的处置是核对签名后刷新 payload-lock.json，
# 而不是把校验关掉。
$vcredistLock = $payloadLock.vcredist
foreach ($property in @('version', 'fileName', 'cacheName', 'url', 'sha256')) {
    if ([string]::IsNullOrWhiteSpace([string]$vcredistLock.$property)) {
        throw "VC++ redistributable payload lock is missing '$property'."
    }
}
if ($vcredistLock.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'VC++ redistributable payload lock sha256 must be exactly 64 hexadecimal characters.'
}
$vcRedistCache = Join-Path $PayloadCacheRoot $vcredistLock.cacheName
if (-not (Test-Path -LiteralPath $vcRedistCache)) {
    Write-Host "Downloading locked VC++ redistributable: $($vcredistLock.url)"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $vcredistLock.url -OutFile $vcRedistCache -UseBasicParsing
}
$actualVcRedistHash = (Get-FileHash -LiteralPath $vcRedistCache -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualVcRedistHash -ne $vcredistLock.sha256.ToLowerInvariant()) {
    throw ("VC++ redistributable SHA-256 mismatch. Expected $($vcredistLock.sha256), got ${actualVcRedistHash}: ${vcRedistCache}. " +
        'aka.ms/vs/17/release is a moving pointer: if Microsoft shipped a newer build, verify the new file''s Authenticode signature and refresh scripts/payload-lock.json.')
}
$vcSignature = Get-AuthenticodeSignature -LiteralPath $vcRedistCache
if ($vcSignature.Status -ne 'Valid') {
    throw "$($vcredistLock.cacheName) Authenticode signature is not valid: $($vcSignature.Status)"
}
if ($vcSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw "$($vcredistLock.cacheName) is not signed by Microsoft: $($vcSignature.SignerCertificate.Subject)"
}
$vcRedistVersion = [string](Get-Item -LiteralPath $vcRedistCache).VersionInfo.ProductVersion
if ($vcRedistVersion -ne [string]$vcredistLock.version) {
    throw "VC++ redistributable version mismatch. Expected $($vcredistLock.version), got '$vcRedistVersion'."
}

if (-not (Test-Path -LiteralPath (Join-Path $srcRoot 'database\V001__initial_schema.sql'))) {
    throw 'Database migration root is missing V001__initial_schema.sql.'
}

Reset-GeneratedDirectory $stageRoot
Reset-GeneratedDirectory $distRoot
Assert-WorkspaceTarget $setupPayloadRoot
New-Item -ItemType Directory -Path $setupPayloadRoot -Force | Out-Null
$existingPayloadZip = Join-Path $setupPayloadRoot 'server-payload.zip'
if (Test-Path -LiteralPath $existingPayloadZip) {
    Remove-Item -LiteralPath $existingPayloadZip -Force
}

$apiPublish = Join-Path $stageRoot 'api'
$agentPublish = Join-Path $stageRoot 'agent'
$trayPublish = Join-Path $stageRoot 'tray'
$agentSetupPublish = Join-Path $stageRoot 'agent-setup'
$agentUpdaterPublish = Join-Path $stageRoot 'agent-updater'
$serverSetupPublish = Join-Path $stageRoot 'server-setup'
$serverTrayPublish = Join-Path $stageRoot 'server-tray'
$databaseStage = Join-Path $stageRoot 'database'
$postgresStage = Join-Path $stageRoot 'postgresql'
$postgresExtract = Join-Path $stageRoot 'postgresql-extract'
New-Item -ItemType Directory -Path $databaseStage, $postgresStage, $postgresExtract -Force | Out-Null

Expand-Archive -LiteralPath $postgresArchive -DestinationPath $postgresExtract -Force
$postgresRoots = @(
    Get-Item -LiteralPath $postgresExtract
    Get-ChildItem -LiteralPath $postgresExtract -Directory -Force
) | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\initdb.exe') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\pg_ctl.exe') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\pg_config.exe') -PathType Leaf)
}
if ($postgresRoots.Count -ne 1) {
    throw "Locked PostgreSQL archive must contain exactly one binary root with initdb.exe, pg_ctl.exe and pg_config.exe; found $($postgresRoots.Count)."
}
$postgresRoot = $postgresRoots[0].FullName
$postgresVersion = (& (Join-Path $postgresRoot 'bin\pg_config.exe') --version).Trim()
$expectedPostgresVersion = ([string]$postgresLock.version) -replace '-.*$', ''
if ($postgresVersion -notmatch "\b$([regex]::Escape($expectedPostgresVersion))\b") {
    throw "PostgreSQL payload version mismatch. Expected $expectedPostgresVersion, got '$postgresVersion'."
}

Invoke-Dotnet @('restore', (Join-Path $srcRoot 'src\BackupMonitor.Api\BackupMonitor.Api.csproj'), '-r', $Runtime)
Invoke-Dotnet @('restore', (Join-Path $srcRoot 'agent\BackupMonitor.Agent\BackupMonitor.Agent.csproj'), '-r', $Runtime)
Invoke-Dotnet @('restore', (Join-Path $srcRoot 'agent\BackupMonitor.Agent.Tray\BackupMonitor.Agent.Tray.csproj'), '-r', $Runtime)
Invoke-Dotnet @('restore', (Join-Path $srcRoot 'agent\BackupMonitor.Agent.Setup\BackupMonitor.Agent.Setup.csproj'), '-r', $Runtime)
Invoke-Dotnet @('restore', (Join-Path $srcRoot 'agent\BackupMonitor.Agent.Updater\BackupMonitor.Agent.Updater.csproj'), '-r', $Runtime)
Invoke-Dotnet @('restore', (Join-Path $srcRoot 'server\BackupMonitor.Server.Setup\BackupMonitor.Server.Setup.csproj'), '-r', $Runtime)
Invoke-Dotnet @('restore', (Join-Path $srcRoot 'server\BackupMonitor.Server.Tray\BackupMonitor.Server.Tray.csproj'), '-r', $Runtime)
Invoke-Dotnet @('publish', (Join-Path $srcRoot 'src\BackupMonitor.Api\BackupMonitor.Api.csproj'), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore', '-o', $apiPublish)
Invoke-Dotnet @('publish', (Join-Path $srcRoot 'agent\BackupMonitor.Agent\BackupMonitor.Agent.csproj'), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore', '-o', $agentPublish)
Invoke-Dotnet @('publish', (Join-Path $srcRoot 'agent\BackupMonitor.Agent.Tray\BackupMonitor.Agent.Tray.csproj'), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore', '-o', $trayPublish)
Invoke-Dotnet @('publish', (Join-Path $srcRoot 'agent\BackupMonitor.Agent.Setup\BackupMonitor.Agent.Setup.csproj'), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $agentSetupPublish)
# 升级执行器（R20）。单文件发布：它装在客户端安装目录**之外**的同级目录里，
# 换文件时不能被锁住；单文件让那个目录的安装与卸载只需要管一个文件。
Invoke-Dotnet @('publish', (Join-Path $srcRoot 'agent\BackupMonitor.Agent.Updater\BackupMonitor.Agent.Updater.csproj'), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $agentUpdaterPublish)

# 服务端托盘（R6）与 API 一起装进安装目录：payload 里的 api\ 目录就是安装目录本身。
# 两者都是同一 RID 的自包含发布，共用的运行时文件逐字节相同，覆盖是安全的——
# 客户端包里 Agent + Agent.Tray 用的也是这个办法。
# 排除 .pdb：调试符号对运行没用，只是把安装包撑大。
Invoke-Dotnet @('publish', (Join-Path $srcRoot 'server\BackupMonitor.Server.Tray\BackupMonitor.Server.Tray.csproj'), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore', '-o', $serverTrayPublish)
if (-not (Test-Path -LiteralPath (Join-Path $serverTrayPublish 'BackupMonitor.Server.Tray.exe'))) {
    throw 'Server tray publish did not produce BackupMonitor.Server.Tray.exe.'
}
Get-ChildItem -LiteralPath $serverTrayPublish -Force |
    Where-Object { $_.Extension -ne '.pdb' } |
    Copy-Item -Destination $apiPublish -Recurse -Force

New-Item -ItemType Directory -Path (Join-Path $apiPublish 'wwwroot\downloads') -Force | Out-Null
$agentExe = Join-Path $agentPublish 'BackupMonitor.Agent.exe'
$trayExe = Join-Path $trayPublish 'BackupMonitor.Agent.Tray.exe'
if (-not (Test-Path -LiteralPath $agentExe) -or -not (Test-Path -LiteralPath $trayExe)) {
    throw 'Self-contained Agent/Tray publish did not produce the expected executables.'
}

$clientPackageStage = Join-Path $stageRoot 'client-package'
New-Item -ItemType Directory -Path $clientPackageStage -Force | Out-Null
# 客户端 ZIP 必须是一份能直接跑起来的自包含发布。只放两个 apphost exe 的话，
# 运行时、hostfxr 和 BackupMonitor.Agent.dll 全都不在包里，解压出来根本起不来；
# install-agent.ps1 也是按「把包根目录整体拷进安装目录」来写的。
# 排除 .pdb：调试符号对运行没用，只是让下载体积翻倍。
foreach ($publishDir in @($agentPublish, $trayPublish)) {
    Get-ChildItem -LiteralPath $publishDir -Force |
        Where-Object { $_.Extension -ne '.pdb' } |
        Copy-Item -Destination $clientPackageStage -Recurse -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $clientPackageStage 'BackupMonitor.Agent.dll'))) {
    throw 'Client package is missing BackupMonitor.Agent.dll; the self-contained publish was not staged.'
}
# 升级执行器放进包里的 updater\ 子目录（R20）。安装器和 install-agent.ps1
# 都把这个子目录单独装到 …\BackupMonitor\Updater，而不是铺进安装目录——
# 升级时安装目录整个会被改名，执行器住在里面就会锁住自己。
$updaterExe = Join-Path $agentUpdaterPublish 'BackupMonitor.Agent.Updater.exe'
if (-not (Test-Path -LiteralPath $updaterExe)) {
    throw 'Agent updater publish did not produce BackupMonitor.Agent.Updater.exe.'
}
$updaterStage = Join-Path $clientPackageStage 'updater'
New-Item -ItemType Directory -Path $updaterStage -Force | Out-Null
Copy-Item -LiteralPath $updaterExe -Destination $updaterStage -Force

Copy-Item -LiteralPath (Join-Path $srcRoot 'agent\BackupMonitor.Agent\appsettings.json') -Destination (Join-Path $clientPackageStage 'appsettings.json') -Force
Copy-Item -LiteralPath (Join-Path $srcRoot 'agent\BackupMonitor.Agent\install-agent.ps1') -Destination (Join-Path $clientPackageStage 'install-agent.ps1') -Force
Copy-Item -LiteralPath (Join-Path $srcRoot 'agent\BackupMonitor.Agent\uninstall-agent.ps1') -Destination (Join-Path $clientPackageStage 'uninstall-agent.ps1') -Force
Copy-Item -LiteralPath (Join-Path $srcRoot 'agent\BackupMonitor.Agent\README.md') -Destination (Join-Path $clientPackageStage 'README.md') -Force
# 现场自检脚本必须与客户端包同行：P1（PowerShell 版本）和 P2（缺 UCRT）在现场
# 表现都是「装不上」，其中缺 UCRT 那条连日志都不会留。这个纯批处理脚本是双击
# 安装程序之前唯一能把问题指出来的东西。
Copy-Item -LiteralPath (Join-Path $srcRoot 'agent\BackupMonitor.Agent\check-prereq.cmd') -Destination (Join-Path $clientPackageStage 'check-prereq.cmd') -Force

# 随附的 Agent 版本号（R11）。服务端要能回答「这台机器落后了没有」，
# 就必须知道「现在随包发出去的是哪一版」——而那个数字在此之前只存在于
# BackupMonitor.Agent.csproj 里，运行期无从得知。
# 取自实际发布出来的那个 exe，不是从 csproj 抄一份：抄的那份迟早和发布物分家。
$agentVersion = (Get-Item -LiteralPath $agentExe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($agentVersion)) {
    throw 'Unable to read the published agent version from BackupMonitor.Agent.exe.'
}
Set-Content -LiteralPath (Join-Path $apiPublish 'wwwroot\downloads\agent-version.txt') -Value $agentVersion.Trim() -Encoding utf8 -NoNewline

$clientZip = Join-Path $apiPublish 'wwwroot\downloads\BackupMonitor.Agent.zip'
# 这几个名字与 AgentInstaller.ValidatePayload / install-agent.ps1 的期望一致：
# 少任何一个，客户端解压出来都起不来。
Invoke-VerifiedCompress `
    -Path (Join-Path $clientPackageStage '*') `
    -DestinationPath $clientZip `
    -RequiredEntry @(
        'BackupMonitor.Agent.exe',
        'BackupMonitor.Agent.dll',
        'BackupMonitor.Agent.Tray.exe',
        'updater/BackupMonitor.Agent.Updater.exe',
        'appsettings.json',
        'install-agent.ps1',
        'check-prereq.cmd')

# 升级包的 SHA-256 随包写出（R20）。下发表单此前要人工把 64 位哈希抄进去，
# 而手抄一个 64 位十六进制本身就是个故障源：抄错一位的表现是每台机器
# 都下载成功、校验失败，那时人只会怀疑包坏了。
$clientZipHash = (Get-FileHash -LiteralPath $clientZip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$($clientZip).sha256" -Value $clientZipHash -Encoding utf8 -NoNewline
$clientSetup = Join-Path $agentSetupPublish 'BackupMonitor.Agent.Setup.exe'
Invoke-ResilientCopy -Path $clientSetup -Destination (Join-Path $apiPublish 'wwwroot\downloads\BackupMonitor.Agent.Setup.exe')

# 下载页的另外两个入口：前置运行库和现场自检脚本。两者都要单独可下载——
# 缺 UCRT 的机器连安装程序都起不来，不可能靠包里的东西自救。
Invoke-ResilientCopy -Path $vcRedistCache -Destination (Join-Path $apiPublish "wwwroot\downloads\$($vcredistLock.fileName)")
Copy-Item -LiteralPath (Join-Path $srcRoot 'agent\BackupMonitor.Agent\check-prereq.cmd') -Destination (Join-Path $apiPublish 'wwwroot\downloads\check-prereq.cmd') -Force

$migrationFiles = Get-ChildItem -LiteralPath (Join-Path $srcRoot 'database') -Filter '*.sql' -File
if ($migrationFiles.Count -eq 0) {
    throw 'Database migration root contains no SQL files.'
}
$migrationFiles | Copy-Item -Destination $databaseStage -Force
New-Item -ItemType Directory -Path (Join-Path $postgresStage 'bin'), (Join-Path $postgresStage 'lib'), (Join-Path $postgresStage 'share') -Force | Out-Null
foreach ($directoryName in @('bin', 'lib', 'share')) {
    $sourceDirectory = Join-Path $postgresRoot $directoryName
    $targetDirectory = Join-Path $postgresStage $directoryName
    Get-ChildItem -LiteralPath $sourceDirectory -Force | Copy-Item -Destination $targetDirectory -Recurse -Force
}
Get-ChildItem -LiteralPath $postgresRoot -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(?i:.*license.*|COPYRIGHT|NOTICE.*|README.*)$' } |
    Copy-Item -Destination $postgresStage -Force

$payloadManifest = [ordered]@{
    postgresql = [ordered]@{
        version = $postgresVersion
        archive = $postgresLock.archiveName
        source_url = $postgresLock.url
        archive_sha256 = $actualArchiveHash
        initdb_sha256 = (Get-FileHash -LiteralPath (Join-Path $postgresRoot 'bin\initdb.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        pg_ctl_sha256 = (Get-FileHash -LiteralPath (Join-Path $postgresRoot 'bin\pg_ctl.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    runtime = $Runtime
}
$payloadManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stageRoot 'payload-manifest.json') -Encoding UTF8

$payloadZip = Join-Path $setupPayloadRoot 'server-payload.zip'
# 迁移脚本逐个点名：新加一条迁移却没进 payload，表现是装完之后某张表/某个列不存在，
# 要到运行期才炸。客户端下载入口同理——缺了它网页上那个下载按钮就是 404。
$payloadRequired = @(
    'api\BackupMonitor.Api.exe',
    'api\BackupMonitor.Server.Tray.exe',
    'api\wwwroot\downloads\BackupMonitor.Agent.zip',
    'api\wwwroot\downloads\BackupMonitor.Agent.zip.sha256',
    'api\wwwroot\downloads\agent-version.txt',
    'api\wwwroot\downloads\BackupMonitor.Agent.Setup.exe',
    'api\wwwroot\downloads\VC_redist.x64.exe',
    'api\wwwroot\downloads\check-prereq.cmd',
    'postgresql\bin\initdb.exe')
$payloadRequired += $migrationFiles | ForEach-Object { "database\$($_.Name)" }
Invoke-VerifiedCompress `
    -Path @($apiPublish, $databaseStage, $postgresStage) `
    -DestinationPath $payloadZip `
    -RequiredEntry $payloadRequired

Invoke-Dotnet @('publish', (Join-Path $srcRoot 'server\BackupMonitor.Server.Setup\BackupMonitor.Server.Setup.csproj'), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $serverSetupPublish)

$serverSetup = Join-Path $serverSetupPublish 'BackupMonitor.Server.Setup.exe'
$agentSetup = Join-Path $agentSetupPublish 'BackupMonitor.Agent.Setup.exe'
if (-not (Test-Path -LiteralPath $serverSetup) -or -not (Test-Path -LiteralPath $agentSetup)) {
    throw 'Final setup publish did not produce both setup executables.'
}
Invoke-ResilientCopy -Path $serverSetup -Destination (Join-Path $distRoot 'BackupMonitor.Server.Setup.exe')
Invoke-ResilientCopy -Path $agentSetup -Destination (Join-Path $distRoot 'BackupMonitor.Agent.Setup.exe')

# 前置条件自检脚本必须与安装包同行：目标机器缺 UCRT 时安装程序压根起不来，
# 只有这个脚本能在双击 exe 之前把问题指出来。
Copy-Item -LiteralPath (Join-Path $repoRoot 'deploy\Check-Prerequisites.ps1') -Destination (Join-Path $distRoot 'Check-Prerequisites.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'deploy\Install-UcrtPrerequisites.ps1') -Destination (Join-Path $distRoot 'Install-UcrtPrerequisites.ps1') -Force
# Check-Prerequisites.ps1 需要 PowerShell 才能跑，而「PowerShell 太旧」本身就是一种
# 装不上的原因。check-prereq.cmd 是纯批处理，任何 Windows 上都能双击，作为兜底同行。
Copy-Item -LiteralPath (Join-Path $srcRoot 'agent\BackupMonitor.Agent\check-prereq.cmd') -Destination (Join-Path $distRoot 'check-prereq.cmd') -Force

# ---- 前置运行库 ----------------------------------------------------------
# 内置 PostgreSQL 是 MSVC 编译的，直接导入 vcruntime140.dll / vcruntime140_1.dll /
# msvcp140.dll。这三个任何 Windows 版本都不自带，缺了安装能装完但数据库服务起不来，
# 而且报的是另一个 DLL 缺失，很容易被当成新问题。所以必须与安装包同行。
#
# 二进制本体在脚本前半段已经按 payload-lock.json 取得并校验过（SHA-256 + Authenticode），
# 这里只负责摆放，不再重复下载与校验——两处各校验一次的话，迟早会有一处被改漏。
$prerequisiteRoot = Join-Path $distRoot 'prerequisites'
New-Item -ItemType Directory -Path $prerequisiteRoot -Force | Out-Null
Invoke-ResilientCopy -Path $vcRedistCache -Destination (Join-Path $prerequisiteRoot 'vc_redist.x64.exe')
Copy-Item -LiteralPath (Join-Path $repoRoot 'deploy\PREREQUISITES.md') -Destination (Join-Path $prerequisiteRoot 'README.md') -Force
"{0}  vc_redist.x64.exe  ({1})" -f $actualVcRedistHash, $vcRedistVersion |
    Set-Content -LiteralPath (Join-Path $prerequisiteRoot 'SHA256SUMS.txt') -Encoding ASCII

$sumLines = foreach ($name in @('BackupMonitor.Server.Setup.exe', 'BackupMonitor.Agent.Setup.exe')) {
    $file = Join-Path $distRoot $name
    "{0}  {1}" -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), $name
}
$sumLines | Set-Content -LiteralPath (Join-Path $distRoot 'SHA256SUMS.txt') -Encoding ASCII
Copy-Item -LiteralPath (Join-Path $stageRoot 'payload-manifest.json') -Destination (Join-Path $distRoot 'payload-manifest.json') -Force

Write-Host "Turnkey build complete: $distRoot"
Get-ChildItem -LiteralPath $distRoot | Select-Object Name, Length
