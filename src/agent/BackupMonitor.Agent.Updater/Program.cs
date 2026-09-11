using BackupMonitor.Agent.Updater;

// BackupMonitor Agent 升级执行器（整改清单 2026-09-10 · R20）。
//
// 为什么要有这么个独立程序：**Windows 服务不能替换自己正在运行的程序文件**——
// 进程活着的时候它的 exe 和已加载的 dll 全都被锁住，而 Agent 是自包含发布，
// 一个包 502 个文件。所以必须有一个住在被替换范围之外的第三方来做这次交换。
// 它装在安装目录的同级目录（…\BackupMonitor\Updater），升级包覆盖不到它。
//
// 退出码：0 成功；1 已回滚（旧版本还在跑）；2 连动都没动（参数或前置检查不过）。

var options = UpdaterOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(UpdaterOptions.Usage);
    return 2;
}

using var log = new UpdaterLog(options.DataDirectory);
log.Info($"升级执行器启动：目标版本 {options.TargetVersion}");
log.Info($"安装目录 {options.InstallDirectory}");
log.Info($"升级包目录 {options.SourceDirectory}");

var runner = new UpgradeRunner(options, log);
try
{
    return (int)runner.Run();
}
catch (Exception ex)
{
    // 走到这里说明连 UpgradeRunner 自己的兜底都没接住。日志与结果文件必须留下来：
    // 升级失败的机器可能再也不上线，而那时这两个文件是现场唯一的证据。
    log.Error("升级执行器异常终止", ex);
    UpgradeResultFile.Write(options, "failed", $"升级执行器异常终止：{ex.Message}", log);
    return 1;
}
