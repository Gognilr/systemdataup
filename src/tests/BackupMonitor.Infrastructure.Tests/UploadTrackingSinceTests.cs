using System.Text.Json;
using BackupMonitor.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 「上次成功备份」的记录起点。
///
/// 现场报上来的问题：一台机器今天早晨明明备份成功了，升级之后托盘写着「还没有过」。
/// 不是统计错了——`LastSuccessfulUploadAtUtc` 是后加的字段，旧版本从不写它，
/// 升上来的 state.json 里根本没有这个键。
///
/// 真正的毛病在于那句话本身：**「还没有过」在「全新装机」和「刚升级完」两种处境下
/// 含义完全相反**，而看托盘的人没有线索去分辨。补一个记录起点，那句话才说得全。
/// </summary>
public sealed class UploadTrackingSinceTests : IDisposable
{
    private readonly string _dataDirectory;

    public UploadTrackingSinceTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "bm-agent-tracking-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch { /* 测试清理失败不阻断 */ }
    }

    [Fact]
    public void 旧版本升上来的状态文件会补上记录起点()
    {
        // 旧版 state.json：有身份，没有 uploadTrackingSinceUtc，也没有 lastSuccessfulUploadAtUtc
        var before = DateTime.UtcNow.AddSeconds(-1);
        File.WriteAllText(
            Path.Combine(_dataDirectory, "state.json"),
            """{"machineId":"OLD-MACHINE","clientId":"11111111-1111-1111-1111-111111111111"}""");

        var state = NewStateStore().Snapshot();

        Assert.Null(state.LastSuccessfulUploadAtUtc);
        Assert.NotNull(state.UploadTrackingSinceUtc);
        Assert.True(state.UploadTrackingSinceUtc >= before, "记录起点应当是升上来的这一刻");
        // 身份不能在这一步被冲掉——补字段是加一笔，不是重写状态
        Assert.Equal("OLD-MACHINE", state.MachineId);
    }

    [Fact]
    public void 记录起点只写一次不会被后续启动刷新()
    {
        var first = NewStateStore().Snapshot().UploadTrackingSinceUtc;
        Assert.NotNull(first);

        var second = NewStateStore().Snapshot().UploadTrackingSinceUtc;

        Assert.Equal(first, second);
    }

    [Fact]
    public void 成功上传之后记录的是上传时刻而不是起点()
    {
        var store = NewStateStore();
        var since = store.Snapshot().UploadTrackingSinceUtc;

        var uploadedAt = DateTime.UtcNow;
        store.Update(s => s.LastSuccessfulUploadAtUtc = uploadedAt);

        var reloaded = NewStateStore().Snapshot();
        Assert.Equal(uploadedAt, reloaded.LastSuccessfulUploadAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(since, reloaded.UploadTrackingSinceUtc);
    }

    /// <summary>EnforceAcl 关掉：测试跑在临时目录上，不需要也不该改那里的 ACL。</summary>
    private AgentStateStore NewStateStore() =>
        new(Options.Create(new AgentOptions { DataDirectory = _dataDirectory, EnforceAcl = false }),
            NullLogger<AgentStateStore>.Instance);
}
