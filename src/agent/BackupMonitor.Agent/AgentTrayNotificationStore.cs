using System.Text.Json;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>
/// Agent 与登录用户托盘进程之间的本地、短期消息队列。
/// 文件采用临时文件替换，避免托盘在写入过程中读到半截 JSON。
/// </summary>
public sealed class AgentTrayNotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<AgentTrayNotificationStore> _logger;
    private readonly object _sync = new();
    private readonly string _path;

    public AgentTrayNotificationStore(
        IOptions<AgentOptions> options,
        ILogger<AgentTrayNotificationStore> logger)
    {
        _logger = logger;
        var directory = options.Value.ExpandedDataDirectory;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "tray-notifications.json");
    }

    public bool TryEnqueue(AgentNotificationDto notification)
    {
        if (notification.Id == Guid.Empty)
            return false;

        lock (_sync)
        {
            try
            {
                var now = DateTime.UtcNow;
                var rows = LoadUnsafe()
                    .Where(n => n.CreatedAt.ToUniversalTime() >= now.AddHours(-48))
                    .ToList();

                if (rows.Any(n => n.Id == notification.Id))
                    return true;

                rows.Add(notification);
                rows = rows
                    .OrderBy(n => n.CreatedAt)
                    .ThenBy(n => n.Id)
                    .TakeLast(100)
                    .ToList();
                SaveUnsafe(rows);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "无法写入 Agent 托盘消息队列");
                return false;
            }
        }
    }

    private List<AgentNotificationDto> LoadUnsafe()
    {
        if (!File.Exists(_path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<AgentNotificationDto>>(
                       File.ReadAllText(_path), JsonOptions)
                   ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Agent 托盘消息队列格式无效，将重新建立队列");
            return [];
        }
    }

    private void SaveUnsafe(IReadOnlyList<AgentNotificationDto> rows)
    {
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(rows, JsonOptions));
        File.Move(tempPath, _path, true);
    }
}
