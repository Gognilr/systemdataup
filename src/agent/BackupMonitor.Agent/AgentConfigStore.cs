using System.Text.Json;
using BackupMonitor.Shared.Security;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

public sealed class AgentConfigStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AgentConfigStore(IOptions<AgentOptions> options)
    {
        var directory = options.Value.ExpandedDataDirectory;
        SecureFileSystem.CreateDirectory(directory);
        _path = Path.Combine(directory, "config.json");
    }

    public AgentConfigResponse? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentConfigResponse>(File.ReadAllText(_path), _json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(AgentConfigResponse config) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(config, _json));
}
