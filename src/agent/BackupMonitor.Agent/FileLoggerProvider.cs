using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>
/// Agent 的滚动文件日志。
///
/// Agent 以 Windows 服务运行，默认只有事件日志一条出路。事件日志在排障时非常别扭：
/// 要开事件查看器、按来源筛、逐条点开看详情，异常堆栈还经常被截断。
/// 出问题的机器往往在客户现场，让对方"去事件查看器里找找"基本等于拿不到信息。
///
/// 因此额外落一份纯文本日志到数据目录下，出问题时让对方把文件发过来即可。
/// 刻意不引第三方日志库：Agent 要能在完全离线的机器上构建和运行。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogWriter _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    public FileLoggerProvider(IOptions<AgentOptions> options)
    {
        _writer = new FileLogWriter(
            Path.Combine(options.Value.ExpandedDataDirectory, "logs"),
            options.Value.LogRetentionDays);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _writer));

    public void Dispose() => _writer.Dispose();
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLogWriter _writer;

    public FileLogger(string category, FileLogWriter writer)
    {
        // 类别名带完整命名空间，日志里只留最后一段，够定位又不占满行宽。
        var lastDot = category.LastIndexOf('.');
        _category = lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
        _writer = writer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var builder = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ')
            .Append(Level(logLevel))
            .Append(" [")
            .Append(_category)
            .Append("] ")
            .Append(formatter(state, exception));

        if (exception is not null)
            builder.AppendLine().Append(exception);

        _writer.Write(builder.ToString());
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };
}

/// <summary>
/// 按天切分的日志写入器。写日志本身绝不能把 Agent 弄崩，
/// 因此所有失败都静默吞掉——日志是排障手段，不是业务功能。
/// </summary>
internal sealed class FileLogWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly int _retentionDays;
    private DateOnly _currentDay;
    private StreamWriter? _writer;
    private bool _disposed;

    public FileLogWriter(string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = Math.Clamp(retentionDays, 1, 365);
    }

    public void Write(string line)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || today != _currentDay)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_directory);
                    _currentDay = today;
                    _writer = new StreamWriter(
                        new FileStream(
                            Path.Combine(_directory, $"agent-{today:yyyyMMdd}.log"),
                            FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true
                    };
                    RemoveExpiredFiles();
                }

                _writer.WriteLine(line);
            }
            catch
            {
                // 磁盘满、权限不足、文件被占用——都不该影响 Agent 本职工作。
                _writer = null;
            }
        }
    }

    private void RemoveExpiredFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retentionDays);
            foreach (var path in Directory.EnumerateFiles(_directory, "agent-*.log"))
            {
                if (File.GetLastWriteTime(path) < cutoff)
                    File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响写入。
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
