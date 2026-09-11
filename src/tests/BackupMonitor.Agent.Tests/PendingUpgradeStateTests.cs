using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent.Tests;

/// <summary>
/// 暂存升级包的落地与读回（整改清单 2026-09-10 · R20）。
///
/// 这一组存在的理由就是那个缺陷本身：整改之前 <c>pending-update.json</c>
/// **全仓库只有一处写入、零个读取方**，于是「升级成功」这句话说的只是
/// 「包解压好了」，而运行的程序一个字节没变。
/// 读取方是新加的，它必须能把写下去的东西原样读回来——读不回来，
/// 升级就又回到了「只暂存不生效」。
/// </summary>
public class PendingUpgradeStateTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "bm-agent-upgrade-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public async Task 暂存记录写下去之后能原样读回来()
    {
        var coordinator = CreateCoordinator();
        var commandId = Guid.NewGuid();
        await coordinator.WritePendingAsync(new PendingUpdate
        {
            Version = "1.2.0",
            PayloadDirectory = Path.Combine(_dataDirectory, "updates", "1.2.0", "payload"),
            Sha256 = new string('a', 64),
            StagedAtUtc = DateTime.UtcNow,
            CommandId = commandId
        }, CancellationToken.None);

        var pending = coordinator.ReadPending();

        Assert.NotNull(pending);
        Assert.Equal("1.2.0", pending!.Version);
        // 指令 ID 必须活下来：新版本起来之后正是靠它把结果认领回原来那次下发，
        // 而回报结果是服务端判「真的换过去了」的依据之一。
        Assert.Equal(commandId, pending.CommandId);
    }

    [Fact]
    public void 没有暂存记录时读回空值而不是抛异常()
    {
        Assert.Null(CreateCoordinator().ReadPending());
    }

    [Fact]
    public async Task 清掉之后不再重复触发同一次升级()
    {
        var coordinator = CreateCoordinator();
        await coordinator.WritePendingAsync(new PendingUpdate
        {
            Version = "1.2.0",
            PayloadDirectory = _dataDirectory
        }, CancellationToken.None);

        coordinator.ClearPending();

        Assert.Null(coordinator.ReadPending());
    }

    [Fact]
    public void 找不到升级执行器时返回空而不是猜一个路径()
    {
        // 猜错路径的后果没有上限：那个程序会删掉并重建一整个安装目录。
        // 找不到就明说找不到，Agent 会据此回报「需要人工到这台机器上完成安装」。
        var coordinator = CreateCoordinator(updaterPath: Path.Combine(_dataDirectory, "not-here.exe"));
        Assert.Null(coordinator.ResolveUpdaterPath());
    }

    [Fact]
    public void 暂存目录是空的时候换升级执行器这一步什么都不做()
    {
        var coordinator = CreateCoordinator(updaterPath: NewUpdaterFile("installed"));

        // 绝大多数轮次都走这条路径（每个升级循环都会调一次）。
        // 它必须是纯粹的空操作——在这里抛异常会把整条升级循环带下去。
        coordinator.TryInstallStagedUpdater();
        coordinator.TryStageUpdaterFromPayload();
    }

    [Fact]
    public void 暂存的升级执行器不比本机的新时直接清掉暂存()
    {
        var installed = NewUpdaterFile("installed");
        var staged = Path.Combine(_dataDirectory, "updater-staged");
        Directory.CreateDirectory(staged);
        File.Copy(installed, Path.Combine(staged, "BackupMonitor.Agent.Updater.exe"));

        CreateCoordinator(updaterPath: installed).TryInstallStagedUpdater();

        // 同版本没有替换的必要。留着它的话，每一轮升级循环都会重新比一次版本、
        // 重新判一次进程，而这份文件永远不会被用掉——那是一个不会自己消失的垃圾。
        Assert.False(Directory.Exists(staged));
    }

    [Fact]
    public void 本机没装升级执行器时不擅自造一个出来()
    {
        var staged = Path.Combine(_dataDirectory, "updater-staged");
        Directory.CreateDirectory(staged);
        File.Copy(SampleVersionedFile, Path.Combine(staged, "BackupMonitor.Agent.Updater.exe"));

        // 从老包装上来的机器本来就没有 updater，只能人工升级。
        // 凭空补一个出来只会让现场多出一种没人预料到的状态。
        CreateCoordinator(updaterPath: Path.Combine(_dataDirectory, "not-here.exe"))
            .TryInstallStagedUpdater();

        Assert.False(Directory.Exists(staged));
    }

    /// <summary>带版本资源的真实文件——FileVersionInfo 读不出版本号的文件一律会被跳过。</summary>
    private static string SampleVersionedFile => typeof(AgentUpgradeCoordinator).Assembly.Location;

    private string NewUpdaterFile(string folder)
    {
        var directory = Path.Combine(_dataDirectory, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "BackupMonitor.Agent.Updater.exe");
        File.Copy(SampleVersionedFile, path, overwrite: true);
        return path;
    }

    private AgentUpgradeCoordinator CreateCoordinator(string? updaterPath = null)
    {
        Directory.CreateDirectory(_dataDirectory);
        return new AgentUpgradeCoordinator(
            Options.Create(new AgentOptions { DataDirectory = _dataDirectory, UpdaterPath = updaterPath }),
            NullLogger<AgentUpgradeCoordinator>.Instance);
    }
}
