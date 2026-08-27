using System.Text.Json;
using BackupMonitor.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// Agent 状态文件瘦身（审计 F-08）。
///
/// 候选文件清单（相对路径 + 绝对路径 + 大小 + SHA-256）原先躺在 state.json 里，
/// 而 Snapshot() 是序列化再反序列化的深拷贝、Update() 每次全量落盘——
/// 一个 5 万文件的任务光单个候选就是 10MB 量级的 JSON，主循环每轮要走好几遍。
///
/// 升级兼容是这一批的硬要求：老版本写的 state.json 必须能被读出来并自动迁移，
/// 否则升级后所有候选丢失、已通过预检的备份要重新全量 SHA-256 扫一遍。
/// </summary>
public sealed class AgentStateStoreSlimmingTests : IDisposable
{
    private readonly string _dataDirectory;

    public AgentStateStoreSlimmingTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "bm-agent-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    [Fact]
    public void 五万文件的候选不会把状态文件撑大()
    {
        var store = NewStateStore();
        var files = NewFileStore();
        var taskId = Guid.NewGuid();

        files.Save("candidate-a", BuildFiles(50_000));
        store.Update(s => s.Candidates["candidate-a"] = new LocalCandidateState
        {
            CandidateBackupSetId = Guid.NewGuid(),
            TaskId = taskId,
            CandidateKey = "candidate-a",
            SourceRoot = @"D:\data",
            TotalFiles = 50_000,
            TotalBytes = 50_000L * 1024,
            UpdatedAt = DateTime.UtcNow
        });

        // 摘要在 state.json，清单在 candidates/ 下，两者数量级差两个位
        var stateSize = new FileInfo(Path.Combine(_dataDirectory, "state.json")).Length;
        Assert.True(stateSize < 100 * 1024, $"state.json 实际 {stateSize} 字节，应当在 100KB 量级以内");

        Assert.Equal(50_000, files.Load("candidate-a").Count);
    }

    [Fact]
    public void 老格式状态文件能被读出并自动迁移()
    {
        // 老版本把整份清单写在 Candidates[].Files 里
        var legacy = new AgentState();
        legacy.Candidates["candidate-legacy"] = new LocalCandidateState
        {
            CandidateBackupSetId = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            CandidateKey = "candidate-legacy",
            SourceRoot = @"D:\data",
            TotalFiles = 3,
            TotalBytes = 3072,
            Files = BuildFiles(3),
            UpdatedAt = DateTime.UtcNow
        };
        File.WriteAllText(
            Path.Combine(_dataDirectory, "state.json"),
            JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var store = NewStateStore();
        var files = NewFileStore();

        Assert.Equal(1, store.MigrateEmbeddedCandidateFiles(files));

        // 候选没丢，清单搬到了新位置
        var candidate = Assert.Single(store.Snapshot().Candidates).Value;
        Assert.Equal(3, candidate.TotalFiles);
        Assert.Equal(3, files.Load("candidate-legacy").Count);

        // 迁移是幂等的：搬完之后 state.json 里不再有内嵌清单
        Assert.Null(candidate.Files);
        Assert.Equal(0, store.MigrateEmbeddedCandidateFiles(files));
    }

    [Fact]
    public void 任务删除后候选与清单文件一并消失()
    {
        var store = NewStateStore();
        var files = NewFileStore();
        var liveTaskId = Guid.NewGuid();
        var deadTaskId = Guid.NewGuid();

        foreach (var (key, taskId) in new[] { ("keep", liveTaskId), ("drop", deadTaskId) })
        {
            files.Save(key, BuildFiles(2));
            store.Update(s => s.Candidates[key] = new LocalCandidateState
            {
                TaskId = taskId,
                CandidateKey = key,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var removed = store.PruneCandidates(new HashSet<Guid> { liveTaskId });
        foreach (var key in removed)
            files.Delete(key);

        Assert.Equal(["drop"], removed);
        Assert.Equal(["keep"], store.Snapshot().Candidates.Keys);
        Assert.Empty(files.Load("drop"));
        Assert.Equal(2, files.Load("keep").Count);
    }

    [Fact]
    public void 候选总量上限按最近更新时间淘汰()
    {
        // CandidateKey 里含有状态段，同一个任务反复扫描会不断产生新键，
        // 光靠「任务还在不在」收敛不了，必须再加一道总量闸。
        var store = NewStateStore();
        var taskId = Guid.NewGuid();
        var baseTime = DateTime.UtcNow.AddDays(-10);

        for (var i = 0; i < 10; i++)
        {
            var key = $"candidate-{i:00}";
            store.Update(s => s.Candidates[key] = new LocalCandidateState
            {
                TaskId = taskId,
                CandidateKey = key,
                UpdatedAt = baseTime.AddHours(i)
            });
        }

        var removed = store.PruneCandidates(new HashSet<Guid> { taskId }, maxEntries: 3);

        Assert.Equal(7, removed.Count);
        var survivors = store.Snapshot().Candidates.Keys.OrderBy(k => k).ToList();
        Assert.Equal(["candidate-07", "candidate-08", "candidate-09"], survivors);
    }

    [Fact]
    public void 清单文件名不受候选键里的路径分隔符影响()
    {
        var files = NewFileStore();
        // candidateKey 里带路径分隔符和冒号，直接当文件名会抛异常
        const string key = @"D:\data\2026\业务一|passed";

        files.Save(key, BuildFiles(1));

        Assert.Single(files.Load(key));
    }

    // ---------- 基础设施 ----------

    private AgentStateStore NewStateStore() =>
        new(Options.Create(NewOptions()), NullLogger<AgentStateStore>.Instance);

    private AgentCandidateFileStore NewFileStore() =>
        new(Options.Create(NewOptions()), NullLogger<AgentCandidateFileStore>.Instance);

    /// <summary>EnforceAcl 关掉：测试跑在临时目录上，不需要也不该改那里的 ACL。</summary>
    private AgentOptions NewOptions() => new()
    {
        DataDirectory = _dataDirectory,
        EnforceAcl = false
    };

    private static List<LocalCandidateFile> BuildFiles(int count) =>
        Enumerable.Range(0, count).Select(i => new LocalCandidateFile
        {
            RelativePath = $"data/part-{i:00000}.bak",
            FullPath = $@"D:\data\part-{i:00000}.bak",
            SizeBytes = 1024,
            Sha256 = new string('a', 64)
        }).ToList();
}
