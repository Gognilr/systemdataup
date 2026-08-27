using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

/// <summary>
/// 候选备份集文件清单的独立存储（审计 F-08）。
///
/// 这些清单原先躺在 state.json 的 Candidates 里，而清单是「每个文件的相对路径 +
/// 绝对路径 + 大小 + SHA-256」——一个 5 万文件的任务，单个候选就是 10MB 量级的 JSON。
/// 而 AgentStateStore.Snapshot() 用「序列化再反序列化」做深拷贝、Update() 每次都全量落盘，
/// 于是每分钟几十次、每次几十 MB 的 JSON 序列化加同步写盘，全在同一把锁里。
///
/// 挪出来之后 state.json 只留摘要，清单按候选各存一份，只在真正要上传时才读。
/// 形状参考 <see cref="AgentConfigStore"/>：同一个数据目录，同样的 ACL 处理，
/// 读失败一律当作「没有」而不是抛异常——清单丢了可以重扫，抛异常会让整条指令失败。
/// </summary>
public sealed class AgentCandidateFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly ILogger<AgentCandidateFileStore> _logger;

    public AgentCandidateFileStore(IOptions<AgentOptions> options, ILogger<AgentCandidateFileStore> logger)
    {
        _logger = logger;
        _directory = Path.Combine(options.Value.ExpandedDataDirectory, "candidates");
        SecureFileSystem.CreateDirectory(_directory, options.Value.EnforceAcl);
    }

    public void Save(string candidateKey, IReadOnlyList<LocalCandidateFile> files)
    {
        try
        {
            var path = PathFor(candidateKey);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(files, JsonOptions));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            // 写不进去不该让预检上报失败——上报已经成功了，坏的只是本地缓存；
            // 下次上传时 Load 返回空，指令会以 CANDIDATE_FILES_MISSING 明确失败，
            // 而不是带着半份清单传上去。
            _logger.LogWarning(ex, "候选文件清单写入失败 candidateKey={CandidateKey}", candidateKey);
        }
    }

    public List<LocalCandidateFile> Load(string candidateKey)
    {
        var path = PathFor(candidateKey);
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<LocalCandidateFile>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "候选文件清单读取失败 candidateKey={CandidateKey}", candidateKey);
            return [];
        }
    }

    public void Delete(string candidateKey)
    {
        try
        {
            var path = PathFor(candidateKey);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "候选文件清单删除失败 candidateKey={CandidateKey}", candidateKey);
        }
    }

    /// <summary>
    /// 清单文件名用 candidateKey 的 SHA-256 十六进制。
    /// candidateKey 里含有路径分隔符和业务单元名，不能直接当文件名。
    /// </summary>
    private string PathFor(string candidateKey) =>
        Path.Combine(
            _directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidateKey))).ToLowerInvariant() + ".json");
}
