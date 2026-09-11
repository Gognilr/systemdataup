using System.Text.Json;

namespace BackupMonitor.Agent.Updater;

/// <summary>
/// 升级结果文件（数据目录下的 upgrade-result.json）。
///
/// 它是 updater 与新启动的 Agent 之间唯一的交接点：Agent 起来后读到它，
/// 就把结果连同**自己实际运行的版本号**一起上报给服务端，然后删掉这个文件。
/// 这一条路径是「界面上的成功」与「真的换过去了」之间的那座桥——
/// 旧实现没有它，于是指令回报一句成功就算完事。
///
/// 写文件而不是让 updater 自己调接口：updater 没有客户端证书，
/// 而心跳通道的身份是 mTLS 客户端证书，只有 Agent 自己拿得出来。
/// </summary>
internal static class UpgradeResultFile
{
    public const string FileName = "upgrade-result.json";

    public static void Write(UpdaterOptions options, string status, string? message, UpdaterLog log)
    {
        try
        {
            var path = Path.Combine(options.DataDirectory, FileName);
            var payload = JsonSerializer.Serialize(new
            {
                status,
                targetVersion = options.TargetVersion,
                commandId = options.CommandId,
                message,
                completedAt = DateTime.UtcNow
            });
            File.WriteAllText(path, payload);
            log.Info($"升级结果已写入 {path}：{status}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 结果写不出来只影响「服务端多久知道」，不影响机器本身是否正常——
            // 服务端还有超时判定兜底，那条路径不依赖这个文件。
            log.Error("升级结果文件写入失败", ex);
        }
    }
}
