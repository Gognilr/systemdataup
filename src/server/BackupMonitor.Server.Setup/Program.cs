namespace BackupMonitor.Server.Setup;

internal static class Program
{
    /// <summary>无界面导出配置备份包的命令行开关。</summary>
    private const string ExportBackupPackageSwitch = "--export-backup-package";

    [STAThread]
    private static int Main(string[] args)
    {
        // 无界面导出模式。
        //
        // 存在的理由：管理网页要能远程导出配置备份包，而导出逻辑
        //（ServerMaintenance.ExportBackupPackageAsync）在安装器工程里，API 工程引用不到它——
        // 安装器是 net8.0-windows 且反过来引用 API，做不成项目引用。
        // 让 API 起一个子进程走这条同一份代码，好过在 API 侧照抄一份出来：
        // 抄的那份迟早和这份分家，而分家的结果是「导出的包导不回去」，
        // 恰恰在真要恢复的那天才发现。
        if (TryGetExportTarget(args, out var packagePath))
            return RunHeadlessExport(packagePath);

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(args));
        return 0;
    }

    private static bool TryGetExportTarget(string[] args, out string packagePath)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], ExportBackupPackageSwitch, StringComparison.OrdinalIgnoreCase))
            {
                packagePath = args[i + 1];
                return !string.IsNullOrWhiteSpace(packagePath);
            }
        }

        packagePath = string.Empty;
        return false;
    }

    private static int RunHeadlessExport(string packagePath)
    {
        try
        {
            new ServerMaintenance().ExportBackupPackageAsync(
                    ServerMaintenance.DefaultInstallDirectory,
                    ServerMaintenance.DefaultDataDirectory,
                    packagePath,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            // 错误只走 stderr。调用方（API）会把这段文本回给管理网页，
            // 因此这里绝不能把口令、连接串或密钥路径写进消息里——
            // ServerMaintenance 抛出的异常本身遵守这条口径。
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
