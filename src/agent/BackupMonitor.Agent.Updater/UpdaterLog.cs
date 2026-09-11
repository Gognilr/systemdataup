namespace BackupMonitor.Agent.Updater;

/// <summary>
/// 升级执行器的文件日志。
///
/// 刻意不复用 Agent 那套日志基础设施：这个程序运行的那几分钟里 Agent 是停着的，
/// 而且它要写的恰恰是「Agent 为什么没起来」。日志文件放在数据目录下，
/// 数据目录不在升级覆盖范围内，回滚也不会把它删掉。
/// </summary>
internal sealed class UpdaterLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    public UpdaterLog(string dataDirectory)
    {
        try
        {
            var directory = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"updater-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写不出来不该让升级停在这里：换文件本身还是能做的，
            // 而停在这一步等于「因为记不下来所以不做」。
            Console.Error.WriteLine($"升级日志无法写入：{ex.Message}");
            _writer = null;
        }
    }

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}：{ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            _writer?.WriteLine(line);
        }
    }

    public void Dispose() => _writer?.Dispose();
}
