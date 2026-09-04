using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 「服务端最近一次听到这台客户端说话」的记录者（单例）。
///
/// 存在的理由：存活判定原先唯一的证据是 last_heartbeat_at，而全系统只有心跳那一个端点
/// 会写它。结果是判定与事实脱节——一台正在传 5 GB 备份、每几秒就要领指令 / 报进度 /
/// 传分块的机器，只要心跳被大文件哈希拖住，180 秒后就变「疑似离线」、300 秒后
/// 「离线」并发严重告警。而它正在好好地备份。这种告警响几次之后就没人看了。
///
/// 所以把判据换成「任何一次已认证的 Agent 请求」：那是比心跳更强的存活证据，
/// 而且不需要再加一条探测链路——服务端连不到客户端（Agent 是出站轮询，不监听端口），
/// 「主动去探一下」这条路在这套形态里根本不存在，能做到的最直接的事就是把
/// 已经收到的这些请求算数。
///
/// 写库要限流：分块上传是每个分块一个请求，8 MB 一块的话一次 5 GB 备份就有六百多次，
/// 每次都 UPDATE 一行等于把 clients 表当日志写。同一个客户端最多每
/// <see cref="MinWriteGap"/> 落一次库——存活阈值是 180 秒起步，这个精度绰绰有余。
/// </summary>
public sealed class ClientLastSeenTracker
{
    /// <summary>同一客户端两次落库之间的最小间隔。</summary>
    private static readonly TimeSpan MinWriteGap = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClientLastSeenTracker> _logger;
    private readonly Dictionary<Guid, DateTime> _lastWritten = [];
    private readonly object _gate = new();

    public ClientLastSeenTracker(IServiceScopeFactory scopeFactory, ILogger<ClientLastSeenTracker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 记下「这一刻听到它了」。距上次落库不足 <see cref="MinWriteGap"/> 就直接返回。
    ///
    /// 失败只记日志不抛：这是旁路副作用，一次写不进去的存活时间戳
    /// 绝不能把 Agent 正在做的那件事（领指令、传分块）连累成失败。
    /// </summary>
    public async Task TouchAsync(Guid clientId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_lastWritten.TryGetValue(clientId, out var previous) && now - previous < MinWriteGap)
                return;
            _lastWritten[clientId] = now;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Clients
                .Where(c => c.Id == clientId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeenAt, now), ct);
        }
        catch (OperationCanceledException)
        {
            // 请求被取消（Agent 断开）本身就不是错误，也不值得记一行日志。
        }
        catch (Exception ex)
        {
            // 下次还会再试：这里不把时间戳回退，否则一段时间的数据库抖动会变成
            // 每个请求都重试一次写入，把抖动放大成压力。
            _logger.LogDebug(ex, "记录客户端 {ClientId} 的最近通信时间失败", clientId);
        }
    }
}
