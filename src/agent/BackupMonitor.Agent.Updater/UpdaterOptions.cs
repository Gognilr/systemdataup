namespace BackupMonitor.Agent.Updater;

/// <summary>升级执行器的命令行参数。</summary>
internal sealed class UpdaterOptions
{
    public const string Usage = """
        BackupMonitor.Agent.Updater
          --install-dir  <Agent 安装目录>
          --data-dir     <Agent 数据目录>
          --source       <已解压的升级包目录>
          --version      <目标版本号>
          [--command-id  <下发这次升级的指令 ID>]
          [--service-name <Windows 服务名，默认 "BackupMonitor Agent">]
        """;

    public required string InstallDirectory { get; init; }

    public required string DataDirectory { get; init; }

    public required string SourceDirectory { get; init; }

    public required string TargetVersion { get; init; }

    public string ServiceName { get; init; } = "BackupMonitor Agent";

    public string? CommandId { get; init; }

    /// <summary>
    /// 参数解析。任何一项缺失都返回 null 而不是猜一个默认值：
    /// 这个程序会删掉并重建一整个安装目录，猜错目录的代价没有上限。
    /// </summary>
    public static UpdaterOptions? Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                map[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        string? Get(string key) => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

        var installDir = Get("install-dir");
        var dataDir = Get("data-dir");
        var source = Get("source");
        var version = Get("version");
        if (installDir is null || dataDir is null || source is null || version is null)
            return null;

        return new UpdaterOptions
        {
            InstallDirectory = Path.GetFullPath(installDir),
            DataDirectory = Path.GetFullPath(dataDir),
            SourceDirectory = Path.GetFullPath(source),
            TargetVersion = version,
            ServiceName = Get("service-name") ?? "BackupMonitor Agent",
            CommandId = Get("command-id")
        };
    }
}
