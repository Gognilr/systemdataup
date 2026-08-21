namespace BackupMonitor.Server.Setup;

internal enum PrerequisiteLevel
{
    Ok,
    Warning,
    Blocking
}

internal sealed record PrerequisiteResult(PrerequisiteLevel Level, string Title, string Detail);

/// <summary>
/// 安装前的运行环境自检。
///
/// 注意其覆盖范围：本检查跑在托管代码里，因此只能发现"进程已经起来了、但环境仍然不满足"
/// 的问题。若目标机器彻底缺失 UCRT（Windows Server 2012 未打 KB2999226 的典型症状），
/// apphost 在加载 hostfxr 时就会被系统加载器拦下并弹出"丢失 api-ms-win-crt-*.dll"，
/// 本检查根本没有机会执行——那种场景必须靠 deploy/Check-Prerequisites.ps1 在启动前拦截。
/// 这里仍保留 UCRT 探测，是为了发现"安装程序靠同目录 DLL 副本跑起来了、但系统里其实
/// 装了一半"的情况：装完的服务端不在这个目录下运行，届时会启动失败。
/// </summary>
internal static class PrerequisiteCheck
{
    /// <summary>
    /// Server 2012 / 2012 R2 上 UCRT 只能靠 KB2999226 补齐：报错点名的
    /// api-ms-win-crt-*.dll 是一组转发 DLL，只有这个系统补丁会把它们装进 System32。
    /// vc_redist.x64.exe 在这两个系统上不部署 UCRT（它把 UCRT 当作由 OS 补丁提供的前置），
    /// 实测装完并重启后 System32 里连 ucrtbase.dll 都不会出现，因此它替代不了补丁。
    /// 补丁自身的前置链又随版本不同，顺序不对或包选错都会提示"此更新不适用"。
    /// </summary>
    private static string UcrtGuidance(Version osVersion)
    {
        var isR2 = osVersion >= new Version(6, 3);
        var steps = isR2
            ? "  1. KB2919442（服务堆栈更新）\r\n"
              + "  2. KB2919355（Update 1）\r\n"
              + "  3. KB2999226 —— 包名 Windows8.1-KB2999226-x64.msu（勿选 Windows8-RT-*）"
            : "  1. KB2919442（服务堆栈更新）\r\n"
              + "  2. KB2999226 —— 包名 Windows8-RT-KB2999226-x64.msu（勿选 Windows8.1-*）";

        return $"唯一正规解：安装系统补丁 KB2999226（{(isR2 ? "Server 2012 R2" : "Server 2012")}）。\r\n"
            + "必须按顺序安装，缺前置会提示\"此更新不适用于你的计算机\"：\r\n"
            + steps + "\r\n\r\n"
            + "下载：https://www.catalog.update.microsoft.com/Search.aspx?q=KB2999226\r\n\r\n"
            + "注意：VC++ 2015-2022 运行库（vc_redist.x64.exe）不能替代该补丁：它在本系统上\r\n"
            + "不部署 UCRT，装完重启后 System32 里依然没有 ucrtbase.dll。";
    }

    /// <summary>
    /// Win10 / Server 2016 起 api-ms-win-crt-*.dll 是 API Set 虚拟名称，System32 里没有
    /// 对应文件，只能以 ucrtbase.dll 判断；Server 2012 / 2012 R2 上 KB2999226 会把这些
    /// DLL 实际落盘，因此在老系统上额外核对，用于发现"装了一半"的情况。
    /// </summary>
    private static readonly string[] VisualCRuntimeNames =
    [
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "msvcp140.dll"
    ];

    private static readonly string[] UcrtForwarderNames =
    [
        "api-ms-win-crt-runtime-l1-1-0.dll",
        "api-ms-win-crt-string-l1-1-0.dll",
        "api-ms-win-crt-heap-l1-1-0.dll",
        "api-ms-win-crt-stdio-l1-1-0.dll",
        "api-ms-win-crt-math-l1-1-0.dll",
        "api-ms-win-crt-locale-l1-1-0.dll",
        "api-ms-win-crt-convert-l1-1-0.dll",
        "api-ms-win-crt-time-l1-1-0.dll",
        "api-ms-win-crt-filesystem-l1-1-0.dll"
    ];

    public static IReadOnlyList<PrerequisiteResult> Evaluate()
    {
        var results = new List<PrerequisiteResult>
        {
            CheckOperatingSystem(),
            CheckUniversalCRuntime(),
            CheckVisualCRuntime(),
            CheckProcessArchitecture()
        };
        return results;
    }

    /// <summary>安装前调用。返回 false 表示存在阻断项，调用方应终止安装流程。</summary>
    public static bool EnsureOrPrompt(IWin32Window? owner)
    {
        var results = Evaluate();
        var blocking = results.Where(r => r.Level == PrerequisiteLevel.Blocking).ToList();
        var warnings = results.Where(r => r.Level == PrerequisiteLevel.Warning).ToList();

        if (blocking.Count > 0)
        {
            MessageBox.Show(
                owner,
                "运行环境不满足安装条件：\r\n\r\n" + Describe(blocking),
                "BackupMonitor 安装前自检",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        if (warnings.Count > 0)
        {
            var choice = MessageBox.Show(
                owner,
                "运行环境自检发现以下问题：\r\n\r\n"
                + Describe(warnings)
                + "\r\n\r\n仍要继续安装吗？",
                "BackupMonitor 安装前自检",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            return choice == DialogResult.Yes;
        }

        return true;
    }

    public static string Describe(IEnumerable<PrerequisiteResult> results) =>
        string.Join(
            "\r\n\r\n",
            results.Select(r => $"[{Label(r.Level)}] {r.Title}\r\n{r.Detail}"));

    /// <summary>供诊断面板使用的单行摘要。</summary>
    public static string Summarize()
    {
        var results = Evaluate();
        var worst = results.Max(r => r.Level);
        return worst == PrerequisiteLevel.Ok
            ? "环境自检：通过"
            : "环境自检：\r\n" + Describe(results.Where(r => r.Level != PrerequisiteLevel.Ok));
    }

    private static PrerequisiteResult CheckOperatingSystem()
    {
        var version = Environment.OSVersion.Version;

        if (version < new Version(6, 2))
        {
            return new PrerequisiteResult(
                PrerequisiteLevel.Blocking,
                $"操作系统版本过低（{version}）",
                ".NET 8 最低要求 Windows Server 2012 / Windows 8。请升级操作系统后再安装。");
        }

        if (version < new Version(6, 3))
        {
            return new PrerequisiteResult(
                PrerequisiteLevel.Warning,
                $"Windows Server 2012（{version}）",
                "该系统出厂不含 Universal C Runtime，且微软对它的扩展支持已于 2023-10 结束，\r\n"
                + ".NET 9 及以后版本不再支持该系统。建议尽快迁移到 Server 2016 及以上。\r\n"
                + UcrtGuidance(version));
        }

        if (version < new Version(10, 0))
        {
            return new PrerequisiteResult(
                PrerequisiteLevel.Warning,
                $"Windows Server 2012 R2（{version}）",
                "该系统出厂同样不含 Universal C Runtime（详见下方 UCRT 项），\r\n"
                + "且扩展支持已于 2023-10 结束，.NET 9 及以后版本不再支持该系统。建议规划迁移。");
        }

        return new PrerequisiteResult(PrerequisiteLevel.Ok, $"操作系统版本 {version}", "满足要求。");
    }

    private static PrerequisiteResult CheckUniversalCRuntime()
    {
        var systemDirectory = Environment.SystemDirectory;

        if (!File.Exists(Path.Combine(systemDirectory, "ucrtbase.dll")))
        {
            return new PrerequisiteResult(
                PrerequisiteLevel.Blocking,
                "缺少 Universal C Runtime",
                $"未在 {systemDirectory} 找到 ucrtbase.dll。\r\n" + UcrtGuidance(Environment.OSVersion.Version));
        }

        if (Environment.OSVersion.Version < new Version(10, 0))
        {
            var missing = UcrtForwarderNames
                .Where(name => !File.Exists(Path.Combine(systemDirectory, name)))
                .ToList();

            if (missing.Count > 0)
            {
                return new PrerequisiteResult(
                    PrerequisiteLevel.Blocking,
                    $"Universal C Runtime 安装不完整（缺 {missing.Count} 个文件）",
                    $"未找到：{string.Join("、", missing)}\r\n" + UcrtGuidance(Environment.OSVersion.Version));
            }
        }

        return new PrerequisiteResult(PrerequisiteLevel.Ok, "Universal C Runtime", "已安装。");
    }

    /// <summary>
    /// 内置 PostgreSQL 依赖 MSVC 运行库：postgres.exe 及若干 DLL 直接导入
    /// vcruntime140.dll / vcruntime140_1.dll / msvcp140.dll。这三个任何 Windows 版本都不自带，
    /// pgsqlin 里也没有，只能由 VC++ 2015-2022 运行库提供。
    /// 缺它不影响安装程序自身启动（.NET 自带运行时不依赖 MSVC CRT），
    /// 但装完后数据库服务起不来——报的是另一个 DLL 缺失，很容易被当成第二个 bug。
    /// </summary>
    private static PrerequisiteResult CheckVisualCRuntime()
    {
        var systemDirectory = Environment.SystemDirectory;
        var missing = VisualCRuntimeNames
            .Where(name => !File.Exists(Path.Combine(systemDirectory, name)))
            .ToList();

        if (missing.Count == 0)
            return new PrerequisiteResult(PrerequisiteLevel.Ok, "VC++ 2015-2022 运行库", "已安装。");

        return new PrerequisiteResult(
            PrerequisiteLevel.Blocking,
            $"缺少 VC++ 2015-2022 运行库（{missing.Count} 个文件）",
            $"未找到：{string.Join("、", missing)}\r\n"
            + "内置 PostgreSQL 依赖这些 DLL，缺失会导致数据库服务无法启动。\r\n"
            + "安装：https://aka.ms/vs/17/release/vc_redist.x64.exe\r\n"
            + "注意：在缺 UCRT 的系统上这个安装包装不上东西，需先打完 KB2999226 并重启，\r\n"
            + "再重新运行一次 vc_redist。");
    }

    private static PrerequisiteResult CheckProcessArchitecture()
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return new PrerequisiteResult(
                PrerequisiteLevel.Blocking,
                "非 64 位操作系统",
                "服务端与内置 PostgreSQL 均只提供 x64 版本。");
        }

        return new PrerequisiteResult(PrerequisiteLevel.Ok, "处理器架构", "64 位。");
    }

    private static string Label(PrerequisiteLevel level) => level switch
    {
        PrerequisiteLevel.Blocking => "阻断",
        PrerequisiteLevel.Warning => "警告",
        _ => "通过"
    };
}
