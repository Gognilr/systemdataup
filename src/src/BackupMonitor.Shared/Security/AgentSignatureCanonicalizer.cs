using System.Globalization;
using System.Text;
using System.Text.Json;
using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Shared.Security;

/// <summary>
/// Agent 指令与配置签名的唯一规范化入口。
/// 使用 Unix 毫秒时间戳和固定字段顺序；字符串字段使用 Base64Url 编码，避免分隔符和空值产生歧义。
///
/// 指令与配置各自带版本号，且**刻意不共用一个常量**：两者的灰度节奏不同。
/// 指令签名升到 v2（审计 C-01），配置签名仍是 v1——如果共用一个 CurrentVersion，
/// 「Agent 先于服务端升级」这个发布顺序会让新 Agent 算出 v2 的配置原文、
/// 而尚未升级的服务端还在签 v1，配置直接验不过，Agent 连配置都拿不到。
/// </summary>
public static class AgentSignatureCanonicalizer
{
    /// <summary>指令签名当前版本。v2 起把 taskId / candidateBackupSetId / payload 纳入签名范围。</summary>
    public const string CommandVersion = "v2";

    /// <summary>配置签名版本（未变更）</summary>
    public const string ConfigVersion = "v1";

    private const string NullField = "~";
    private static readonly JsonSerializerOptions LegacyJsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>
    /// 指令签名原文 v2（审计 C-01）。
    ///
    /// v1 只签了 id·nonce·type·clientId·expiresAt——真正决定这条指令做什么的
    /// taskId / candidateBackupSetId / payload 三个字段全在签名之外。
    /// 于是能改写响应体的人可以把 upgrade_agent 的 packageUrl 和 sha256 一起换掉，
    /// Agent 会照攻击者给的哈希校验通过、解压、等待重启切换，而验签全程返回 true。
    /// 指令签名这套机制存在的全部意义就是防这个。
    ///
    /// payload 必须是从库里读回来的那份字符串原文。commands.payload 是 jsonb，
    /// PostgreSQL 会重排键、规范化空白；签名与验签两端拿到的都是同一份规范化后的文本，
    /// 中间**绝对不能**再做一次 JsonSerializer 往返——那会产生一份和库里不一样的文本。
    /// </summary>
    public static byte[] CommandPayload(
        Guid commandId,
        string nonce,
        string commandType,
        Guid clientId,
        DateTime expiresAt,
        Guid? taskId,
        Guid? candidateBackupSetId,
        string? payload) =>
        Utf8(CommandVersion + "|" + JoinFields(
            "command",
            commandId.ToString("D"),
            nonce,
            commandType,
            clientId.ToString("D"),
            ToUnixMilliseconds(expiresAt).ToString(CultureInfo.InvariantCulture),
            taskId?.ToString("D"),
            candidateBackupSetId?.ToString("D"),
            payload));

    /// <summary>
    /// 指令签名原文 v1，供灰度升级窗口验签使用；服务端升级后不再生成此格式。
    /// 灰度窗口结束后连同 <see cref="LegacyCommandPayload"/> 一并删除，
    /// 已登记在 docs/系统审查-整改追踪.md——留着它等于把 C-01 这个漏洞以兼容分支的形式养起来。
    /// </summary>
    public static byte[] CommandPayloadV1(
        Guid commandId,
        string nonce,
        string commandType,
        Guid clientId,
        DateTime expiresAt) =>
        Utf8("v1|" + JoinFields(
            "command",
            commandId.ToString("D"),
            nonce,
            commandType,
            clientId.ToString("D"),
            ToUnixMilliseconds(expiresAt).ToString(CultureInfo.InvariantCulture)));

    /// <summary>兼容更早版本 Agent 的签名原文，供灰度升级窗口验签使用；服务端不再生成此格式。</summary>
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

            // 快速指纹基线：字段追加在任务块末尾，前面的顺序一个都不动。
            // 它决定 Agent 会不会跳过全量哈希，不签名就等于把「让备份静默停摆」
            // 这个开关暴露给任何能改写响应体的人。
            var fingerprints = task.LastQuickFingerprints ?? [];
            fields.Add(fingerprints.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var fingerprint in fingerprints.OrderBy(f => f.ExternalKey, StringComparer.Ordinal))
            {
                fields.Add(fingerprint.ExternalKey);
                fields.Add(fingerprint.QuickFingerprint);
            }
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
        return Utf8(ConfigVersion + "|" + JoinFields(fields.ToArray()));
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
