using BackupMonitor.Shared.Models.Agent;

namespace BackupMonitor.Agent.Tests;

/// <summary>
/// 「还缺哪些块」展开成块号序列。
///
/// 服务端有两种回法：块少时给明细列表，块特别多时给范围。展开错了的后果是漏传，
/// 而漏传的块在会话完成校验之前不会有任何征兆——界面上一路显示正常，
/// 最后卡在「校验失败」，且看不出是哪一步出的问题。
/// </summary>
public class ExpandMissingTests
{
    private const int ChunkSize = 8 * 1024 * 1024;

    [Fact]
    public void 明细列表原样返回()
    {
        var missing = new MissingChunksResponse { Missing = [0, 3, 7] };

        Assert.Equal([0, 3, 7], AgentWorker.ExpandMissing(missing, size: 100, ChunkSize));
    }

    [Fact]
    public void 范围按闭区间展开()
    {
        // 首尾都含。少展开一个尾块，那一块就永远传不上去。
        var missing = new MissingChunksResponse
        {
            MissingRanges = [new ChunkRangeDto { Start = 2, End = 5 }]
        };

        Assert.Equal([2, 3, 4, 5], AgentWorker.ExpandMissing(missing, size: 100, ChunkSize));
    }

    [Fact]
    public void 多段范围拼在一起()
    {
        var missing = new MissingChunksResponse
        {
            MissingRanges =
            [
                new ChunkRangeDto { Start = 0, End = 1 },
                new ChunkRangeDto { Start = 5, End = 5 }
            ]
        };

        Assert.Equal([0, 1, 5], AgentWorker.ExpandMissing(missing, size: 100, ChunkSize));
    }

    [Fact]
    public void 空列表表示一块都不缺()
    {
        // 续传时这是最常见的一种回答：这个文件上一轮已经传完了。
        // 把「空列表」误判成「服务端没说」，会让整个文件重传一遍。
        var missing = new MissingChunksResponse { Missing = [] };

        Assert.Empty(AgentWorker.ExpandMissing(missing, size: 100, ChunkSize));
    }

    [Fact]
    public void 服务端什么都没说时按文件大小全传()
    {
        // Missing 和 MissingRanges 都为 null——只能保守地认为一块都还没传。
        var missing = new MissingChunksResponse();

        var indexes = AgentWorker.ExpandMissing(missing, size: 3L * ChunkSize, ChunkSize).ToList();

        Assert.Equal([0, 1, 2], indexes);
    }

    [Fact]
    public void 最后一块不满也要算进去()
    {
        // 向上取整。按整除算会漏掉末尾那个不满块，而文件末尾恰恰是最常见的不满块。
        var missing = new MissingChunksResponse();

        var indexes = AgentWorker.ExpandMissing(missing, size: 2L * ChunkSize + 1, ChunkSize).ToList();

        Assert.Equal([0, 1, 2], indexes);
    }

    [Fact]
    public void 空文件不产生任何块()
    {
        var missing = new MissingChunksResponse();

        Assert.Empty(AgentWorker.ExpandMissing(missing, size: 0, ChunkSize));
    }
}
