@echo off
chcp 936 >nul 2>nul
setlocal enabledelayedexpansion
title BackupMonitor Agent 安装前自检

rem ===========================================================================
rem  BackupMonitor Agent 安装前自检（纯批处理）
rem
rem  刻意不调用 powershell：PowerShell 版本本身就是被检测项之一，
rem  用 PowerShell 去检测 PowerShell，在它缺失或过旧时只会静默失败。
rem  同样不依赖 .NET：机器缺 UCRT 时，任何 .NET 程序在 Main 之前就被
rem  Windows 加载器拦下，托管代码没有机会打印任何东西。
rem
rem  代码页固定为 936：Server 2012 R2 默认控制台字体是点阵字体，
rem  在 chcp 65001 下渲染不出中文。本文件按 GBK 保存，改动时请保持编码不变。
rem ===========================================================================

set "PROBLEMS="
set "UCRT_FILE=%SystemRoot%\System32\ucrtbase.dll"

echo ===========================================================
echo    BackupMonitor Agent 安装前自检
echo ===========================================================
echo.

rem --- 操作系统版本 ---------------------------------------------------------
rem  ver 的输出形如 Microsoft Windows [版本 6.3.9600] / [Version 10.0.26100.1]，
rem  中英文都是「两个单词 + 左括号词 + 主版本.次版本」，所以第 4、5 个 token
rem  就是主次版本号。解析不出来时不猜，按「未知」提示人工确认。
set "OSMAJOR="
set "OSMINOR="
for /f "tokens=4,5 delims=. " %%a in ('ver') do (
    set "OSMAJOR=%%a"
    set "OSMINOR=%%b"
)
set /a OSNUM=OSMAJOR*100+OSMINOR >nul 2>nul
if not defined OSNUM set "OSNUM=0"
if !OSNUM! LSS 100 (
    echo [警告] 操作系统版本：无法识别（ver 输出：!OSMAJOR!.!OSMINOR!）
    echo         请人工确认本机是 Windows Server 2012 R2 或更高版本。
) else if !OSNUM! LSS 603 (
    echo [阻断] 操作系统版本：!OSMAJOR!.!OSMINOR!，低于 Windows Server 2012 R2 ^(6.3^)
    echo         本产品的客户端支持底线是 Server 2012 R2，本机不在支持范围内。
    set "PROBLEMS=!PROBLEMS! 操作系统版本过低;"
) else if !OSNUM! LSS 1000 (
    echo [通过] 操作系统版本：!OSMAJOR!.!OSMINOR!（Server 2012 R2 / 8.1 一档）
    echo         注意：该档系统出厂不含 UCRT，请特别关注下面的 UCRT 检查项。
) else (
    echo [通过] 操作系统版本：!OSMAJOR!.!OSMINOR!（Server 2016 及以上，UCRT 随系统自带）
)
echo.

rem --- CPU 架构 -------------------------------------------------------------
rem  32 位 cmd 跑在 64 位系统上时 PROCESSOR_ARCHITECTURE 是 x86，
rem  真实架构在 PROCESSOR_ARCHITEW6432 里，两个都要看。
set "ARCH=%PROCESSOR_ARCHITECTURE%"
if defined PROCESSOR_ARCHITEW6432 set "ARCH=%PROCESSOR_ARCHITEW6432%"
if /i "!ARCH!"=="AMD64" (
    echo [通过] CPU 架构：!ARCH!
) else (
    echo [阻断] CPU 架构：!ARCH!
    echo         当前只提供 x64 版本的客户端，本机架构无法安装。
    set "PROBLEMS=!PROBLEMS! CPU 架构非 x64;"
)
echo.

rem --- Universal C Runtime --------------------------------------------------
rem  缺 UCRT 时安装程序在进程启动前就被系统加载器拦下，弹的是
rem  「丢失 api-ms-win-crt-string-l1-1-0.dll」，与我方无关的系统错误框。
if exist "!UCRT_FILE!" (
    echo [通过] Universal C Runtime：已就位（ucrtbase.dll）
) else (
    echo [阻断] Universal C Runtime：缺失（未找到 ucrtbase.dll）
    echo         这就是双击安装程序「弹出丢失 api-ms-win-crt-*.dll」的原因。
    echo         处理：安装系统补丁 KB2999226（走 Windows Update 全量更新即可），
    echo               并安装本目录/下载页的 vc_redist.x64.exe。
    echo               装完需要重启，重启后重新运行本自检。
    set "PROBLEMS=!PROBLEMS! 缺少 UCRT;"
)
echo.

rem --- PowerShell 版本 ------------------------------------------------------
rem  ZIP 部署要跑 install-agent.ps1，脚本声明 #Requires -Version 3.0。
rem  PowerShell 3.0 起引擎版本写在 HKLM\...\PowerShell\3\PowerShellEngine 下；
rem  该键不存在说明本机只有 PowerShell 2.0（或更低）。
set "PSVER="
for /f "tokens=2,*" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine" /v PowerShellVersion 2^>nul ^| find "REG_SZ"') do set "PSVER=%%b"
if not defined PSVER (
    echo [警告] PowerShell：未检测到 3.0 及以上版本
    echo         install-agent.ps1 需要 PowerShell 3.0 以上，本机请改用
    echo         BackupMonitor.Agent.Setup.exe 安装，不要走 ZIP + 脚本部署。
    set "PROBLEMS=!PROBLEMS! PowerShell 低于 3.0（改用 Setup.exe）;"
) else (
    for /f "tokens=1 delims=." %%v in ("!PSVER!") do set "PSMAJOR=%%v"
    set /a PSMAJORNUM=PSMAJOR >nul 2>nul
    if !PSMAJORNUM! LSS 3 (
        echo [警告] PowerShell 版本：!PSVER!，低于 3.0
        echo         请改用 BackupMonitor.Agent.Setup.exe 安装，不要走 ZIP + 脚本部署。
        set "PROBLEMS=!PROBLEMS! PowerShell 低于 3.0（改用 Setup.exe）;"
    ) else (
        echo [通过] PowerShell 版本：!PSVER!
    )
)
echo.

rem --- 系统盘剩余空间 -------------------------------------------------------
rem  dir 的最后一行是「N 个目录 X 可用字节」，token 3 就是字节数。
rem  /-c 去掉千位分隔符。batch 的 set /a 是 32 位有符号，装不下字节数，
rem  所以按位数比：>=11 位必然远超 1GB；<=9 位必然不足 1GB；
rem  正好 10 位时取前 4 位与 1073（1GiB=1073741824）比。
set "FREEBYTES="
for /f "tokens=1,2,3" %%a in ('dir /-c "%SystemDrive%\" 2^>nul') do set "FREEBYTES=%%c"
set "SPACE_OK=1"
if not defined FREEBYTES (
    echo [警告] 系统盘剩余空间：无法读取
    set "SPACE_OK=unknown"
) else if not "!FREEBYTES:~10,1!"=="" (
    echo [通过] 系统盘 %SystemDrive% 剩余空间充足
) else if "!FREEBYTES:~9,1!"=="" (
    echo [警告] 系统盘 %SystemDrive% 剩余空间不足 1 GB（!FREEBYTES! 字节）
    set "PROBLEMS=!PROBLEMS! 系统盘空间不足 1GB;"
) else (
    set "FREEHEAD=!FREEBYTES:~0,4!"
    set /a FREEHEADNUM=FREEHEAD >nul 2>nul
    if !FREEHEADNUM! LSS 1074 (
        echo [警告] 系统盘 %SystemDrive% 剩余空间不足 1 GB（!FREEBYTES! 字节）
        set "PROBLEMS=!PROBLEMS! 系统盘空间不足 1GB;"
    ) else (
        echo [通过] 系统盘 %SystemDrive% 剩余空间充足（!FREEBYTES! 字节）
    )
)
echo.

rem --- 结论 -----------------------------------------------------------------
echo ===========================================================
if defined PROBLEMS (
    echo   需要先处理：!PROBLEMS!
) else (
    echo   可以安装
)
echo ===========================================================
echo.
rem 双击运行时不 pause 住，窗口会一闪而过，现场什么都看不到。
pause
endlocal
