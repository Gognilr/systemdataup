using System.Globalization;
using System.Text;
using System.Text.Json;
using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Shared.Security;

/// <summary>
/// Agent 指令与配置签名的唯一规范化入口。
/// v1 使用 Unix 毫秒时间戳和固定字段顺序；字符串字段使用 Base64Url 编码，避免分隔符和空值产生歧义。
/// </summary>
public static class AgentSignatureCanonicalizer
{
    public const string CurrentVersion = "v1";
    private const string NullField = "~";
    private static readonly JsonSerializerOptions LegacyJsonOptions = new(JsonSerializerDefaults.General);

    public static byte[] CommandPayload(
        Guid commandId,
        string nonce,
        string commandType,
        Guid clientId,
        DateTime expiresAt) =>
        Utf8(CurrentVersion + "|" + JoinFields(
            "command",
            commandId.ToString("D"),
            nonce,
            commandType,
            clientId.ToString("D"),
            ToUnixMilliseconds(expiresAt).ToString(CultureInfo.InvariantCulture)));

    /// <summary>兼容上一版本 Agent 的签名原文，供灰度升级窗口验签使用；服务端不再生成此格式。</summary>
    public static byte[] LegacyCommandPayload(
        Guid commandId,
        string nonce,
        string commandType,
        Guid clientId,
        DateTime expiresAt) =>
        Utf8(string.Join(
            "|",
            commandId.ToString(),
            nonce,
            commandType,
            clientId.ToString(),
            expiresAt.ToString("O")));

    public static byte[] ConfigPayload(
        long version,
        Guid clientId,
        IReadOnlyCollection<AgentTaskConfigDto> tasks,
        IReadOnlyCollection<AgentMonitoredServiceDto> services,
        AgentGlobalSettingsDto globalSettings)
    {
        var fields = new List<string?>
        {
            "config",
            version.ToString(CultureInfo.InvariantCulture),
            clientId.ToString("D"),
            tasks.Count.ToString(CultureInfo.InvariantCulture)
        };

        foreach (var task in tasks.OrderBy(t => t.TaskId))
        {
            fields.AddRange(
            [
                task.TaskId.ToString("D"),
                task.Name,
                task.ApplicationName,
                task.SourcePath,
                task.RecognizerType,
                task.TaskMode,
                task.Enabled ? "1" : "0",
                task.ScanSchedule,
                task.ScheduleTimezone,
                task.StabilityIntervalSeconds.ToString(CultureInfo.InvariantCulture),
                task.MaxStabilityWaitSeconds.ToString(CultureInfo.InvariantCulture),
                task.BandwidthLimitKbps?.ToString(CultureInfo.InvariantCulture),
                task.ChunkSizeBytes.ToString(CultureInfo.InvariantCulture),
                task.RandomDelayMinutes.ToString(CultureInfo.InvariantCulture),
                task.RecognizerConfig,
                task.ConfigVersion.ToString(CultureInfo.InvariantCulture)
            ]);
        }

        fields.Add(services.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var service in services
                     .OrderBy(s => s.ServiceName, StringComparer.Ordinal)
                     .ThenBy(s => s.DisplayName, StringComparer.Ordinal))
        {
            fields.AddRange(
            [
                service.ServiceName,
                service.DisplayName,
                service.ExpectedState,
                service.AlertOnMismatch ? "1" : "0"
            ]);
        }

        fields.Add(globalSettings.HeartbeatIntervalSeconds.ToString(CultureInfo.InvariantCulture));
        fields.Add(globalSettings.MaxConcurrentUploads.ToString(CultureInfo.InvariantCulture));
        return Utf8(CurrentVersion + "|" + JoinFields(fields.ToArray()));
    }

    /// <summary>兼容上一版本 DTO JSON 签名，供灰度升级窗口验签使用。</summary>
    public static byte[] LegacyConfigPayload(
        long version,
        Guid clientId,
        IReadOnlyCollection<AgentTaskConfigDto> tasks,
        IReadOnlyCollection<AgentMonitoredServiceDto> services,
        AgentGlobalSettingsDto globalSettings)
    {
        var canonicalJson = JsonSerializer.Serialize(new
        {
            Version = version,
            tasks,
            services,
            global = globalSettings
        }, LegacyJsonOptions);

        return Utf8(string.Join(
            "|",
            version,
            clientId.ToString(),
            canonicalJson));
    }

    public static long ToUnixMilliseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    private static string JoinFields(params string?[] fields)
    {
        var builder = new StringBuilder();
        foreach (var field in fields)
        {
            builder.Append('|');
            builder.Append(field is null ? NullField : ToBase64Url(field));
        }

        return builder.Length > 0 ? builder.ToString()[1..] : string.Empty;
    }

    private static string ToBase64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
