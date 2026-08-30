using System.Text.Json;
using System.Text.Json.Nodes;

namespace BackupMonitor.Infrastructure.Common;

/// <summary>
/// 预检指令 result_payload 的结构与读写，集中一处。
///
/// 存在的理由是一次真实的故障：一个任务下有多少个业务单元，Agent 就分多少次上报预检结果，
/// 而这些上报共用同一个 commandId（U8 一台机器 18 个账套是常态）。
/// 这块载荷原先只放一个 candidateBackupSetId，于是：
///   一、第 2 个账套的上报撞上「这条指令已经有结果了」被当成重复提交丢掉——
///       不建候选、不下发上传、不告警，界面上一点痕迹都没有；
///   二、整条指令在第 1 个账套就被判成终态，后面的进度上报全部 409，
///       第 1 个账套若恰好是 no_new_backup，另外 17 个有新备份的账套一个都不会传。
///
/// 所以它必须是一份**清单**。顶层的 candidateBackupSetId 保留下来只为兼容旧前端，
/// 取的是第一个通过预检的候选。
/// </summary>
public static class PrecheckResultPayload
{
    private const string CandidatesProperty = "candidates";
    private const string LegacyIdProperty = "candidateBackupSetId";
    private const string ProgressProperty = "progress";
    private const string UnitsProperty = "units";

    /// <summary>清单里的一项：一个业务单元这一次扫出来的结果。</summary>
    /// <param name="CandidateKey">去重键。同一个单元重复上报（指令重试、Agent 重启补报）认它。</param>
    /// <param name="CandidateBackupSetId">服务端为这个单元建/更新的候选备份集。</param>
    /// <param name="Status">该单元的预检结论（passed / no_new_backup / ...，snake_case）。</param>
    /// <param name="UploadCommandId">自动模式下为它下发的上传指令；没下发就是 null。</param>
    /// <param name="UploadState">下发结果：dispatched / queued / already_running / already_done / not_dispatched。</param>
    /// <param name="BusinessUnit">业务单元显示名。界面靠它把这一项和开头报上来的名单对位——
    /// CandidateKey 里虽然也嵌着单元键，但那是内部拼出来的字符串，拿它反解是另一种耦合。</param>
    public readonly record struct Entry(
        string CandidateKey,
        Guid CandidateBackupSetId,
        string Status,
        Guid? UploadCommandId,
        string? UploadState,
        string? BusinessUnit = null);

    public const string StatePassed = "passed";
    public const string StateDispatched = "dispatched";
    public const string StateQueued = "queued";
    public const string StateAlreadyRunning = "already_running";
    public const string StateAlreadyDone = "already_done";
    public const string StateNotDispatched = "not_dispatched";

    /// <summary>解析出清单。载荷缺失、不是对象、或是老格式（只有一个 id）时都返回空清单。</summary>
    public static IReadOnlyList<Entry> Entries(string? payload)
    {
        var root = ParseObject(payload);
        if (root?[CandidatesProperty] is not JsonArray array)
            return [];

        var entries = new List<Entry>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonObject item)
                continue;
            var key = item[nameof(Entry.CandidateKey).ToCamel()]?.GetValue<string>();
            var id = item[nameof(Entry.CandidateBackupSetId).ToCamel()]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key) || !Guid.TryParse(id, out var candidateId))
                continue;

            entries.Add(new Entry(
                key,
                candidateId,
                item[nameof(Entry.Status).ToCamel()]?.GetValue<string>() ?? "",
                Guid.TryParse(item[nameof(Entry.UploadCommandId).ToCamel()]?.GetValue<string>(), out var cmd) ? cmd : null,
                item[nameof(Entry.UploadState).ToCamel()]?.GetValue<string>(),
                item[nameof(Entry.BusinessUnit).ToCamel()]?.GetValue<string>()));
        }

        return entries;
    }

    /// <summary>这个业务单元是不是已经上报过了；是则返回上次给它建的候选。</summary>
    public static Guid? FindCandidate(string? payload, string candidateKey) =>
        Entries(payload).FirstOrDefault(e => string.Equals(e.CandidateKey, candidateKey, StringComparison.Ordinal))
            is { CandidateBackupSetId: var id } && id != Guid.Empty ? id : null;

    public static bool HasCandidates(string? payload) => Entries(payload).Count > 0;

    /// <summary>
    /// 记下一个业务单元的结果。同键覆盖（重试补报走这条），新键追加。
    /// 顺带维护顶层那个兼容用的 candidateBackupSetId。
    /// </summary>
    public static string Upsert(string? payload, Entry entry)
    {
        var root = ParseObject(payload) ?? [];
        if (root[CandidatesProperty] is not JsonArray array)
        {
            array = [];
            root[CandidatesProperty] = array;
        }

        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is JsonObject existing
                && string.Equals(existing[nameof(Entry.CandidateKey).ToCamel()]?.GetValue<string>(), entry.CandidateKey, StringComparison.Ordinal))
                array.RemoveAt(i);
        }

        array.Add(new JsonObject
        {
            [nameof(Entry.CandidateKey).ToCamel()] = entry.CandidateKey,
            [nameof(Entry.CandidateBackupSetId).ToCamel()] = entry.CandidateBackupSetId.ToString(),
            [nameof(Entry.Status).ToCamel()] = entry.Status,
            [nameof(Entry.UploadCommandId).ToCamel()] = entry.UploadCommandId?.ToString(),
            [nameof(Entry.UploadState).ToCamel()] = entry.UploadState,
            [nameof(Entry.BusinessUnit).ToCamel()] = entry.BusinessUnit
        });

        var firstPassed = Entries(root.ToJsonString()).FirstOrDefault(e => e.Status == StatePassed);
        root[LegacyIdProperty] = firstPassed.CandidateBackupSetId == Guid.Empty
            ? null
            : firstPassed.CandidateBackupSetId.ToString();

        return root.ToJsonString();
    }

    /// <summary>
    /// 写进度，同时把已经攒下的清单原样留着。
    /// 整块覆盖的那一版里，第 1 个账套的结果会被第 2 个账套的第一条进度抹掉。
    /// </summary>
    public static string WithProgress(string? payload, object progress, JsonSerializerOptions options)
    {
        var root = ParseObject(payload) ?? [];
        root[ProgressProperty] = JsonNode.Parse(JsonSerializer.Serialize(progress, options));
        return root.ToJsonString();
    }

    /// <summary>
    /// 写下这次扫描一开始枚举出来的业务单元名单。
    ///
    /// 它和 candidates 是两件事：名单是「这次一共要扫哪些」，在读第一个字节之前就定下来了；
    /// candidates 是「已经扫完的有哪些」，边扫边长。两者一减才有「还剩多少」。
    /// </summary>
    public static string WithUnits(string? payload, IEnumerable<string> units)
    {
        var root = ParseObject(payload) ?? [];
        var array = new JsonArray();
        foreach (var unit in units)
            array.Add(unit);
        root[UnitsProperty] = array;
        return root.ToJsonString();
    }

    /// <summary>指令终结时把进度摘掉：它描述的是「正在做」，留着只会让人以为还在跑。</summary>
    public static string? WithoutProgress(string? payload)
    {
        var root = ParseObject(payload);
        if (root is null)
            return payload;
        root.Remove(ProgressProperty);
        return root.ToJsonString();
    }

    private static JsonObject? ParseObject(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            // 载荷坏了不能把整次上报连累成失败：当作没有历史，从这一条重新攒。
            return null;
        }
    }

    private static string ToCamel(this string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
