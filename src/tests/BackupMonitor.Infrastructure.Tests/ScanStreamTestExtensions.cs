using BackupMonitor.Agent;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 扫描器改为逐个业务单元产出结果（IAsyncEnumerable）之后，用例里仍然要一次性拿到全部结果
/// 才好断言。这个扩展只做「收集」这一件事，不改变任何判定。
/// </summary>
internal static class ScanStreamTestExtensions
{
    public static async Task<List<BackupScanResult>> ToListAsync(
        this IAsyncEnumerable<BackupScanResult> scans, CancellationToken ct = default)
    {
        var results = new List<BackupScanResult>();
        await foreach (var scan in scans.WithCancellation(ct))
            results.Add(scan);
        return results;
    }
}
