using System.Text.Json;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Recognition;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Recognition;
using Microsoft.EntityFrameworkCore;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 建任务向导的服务端：浏览客户端目录 → 推断识别规则 → 在快照上预演判定。
/// </summary>
public interface IRecognizerWizardService
{
    /// <summary>下发一条 browse_path 指令。返回 commandId，界面据此轮询。</summary>
    Task<DispatchCommandResponse> BrowseAsync(Guid clientId, BrowseClientPathRequest request, CancellationToken ct = default);

    /// <summary>查询浏览结果。指令未完成时返回状态，完成时带上目录快照。</summary>
    Task<BrowseClientPathResultDto> GetBrowseResultAsync(Guid commandId, CancellationToken ct = default);

    /// <summary>在某次快照的某个目录上推断识别规则，并附带按该规则跑出来的预演结果。</summary>
    Task<RecognizerProposalDto> InferAsync(InferRecognizerRequest request, CancellationToken ct = default);

    /// <summary>拿一条具体规则在快照上预演。改一个勾选就重跑一次，代价是毫秒级。</summary>
    Task<RecognizerPreviewDto> PreviewAsync(PreviewRecognizerRequest request, CancellationToken ct = default);
}

public class RecognizerWizardService : IRecognizerWizardService
{
    /// <summary>
    /// 浏览指令的存活时间。它是交互式的：人在界面上等结果，等不到就会重来一次，
    /// 留一条 24 小时的僵尸指令没有意义——客户端下次上线时突然去枚举一遍磁盘更没有意义。
    /// </summary>
    private static readonly TimeSpan BrowseTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 预演用的稳定观察窗口，取任务默认值（BackupTask.StabilityIntervalSeconds = 600）。
    ///
    /// 此前推断时固定传 0，于是向导说"3 个全部完整"，第二天真扫时刚写完不到 10 分钟的
    /// 单元被判成"仍在变化"。预演的全部价值就是"它现在告诉你的，就是将来会发生的"，
    /// 口径不一致等于把这份承诺撤回了。
    /// </summary>
    private const int DefaultStabilityIntervalSeconds = 600;

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _commands;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;

    public RecognizerWizardService(
        AppDbContext db,
        ICommandDispatcher commands,
        ICurrentContext context,
        IAuditRecorder audit)
    {
        _db = db;
        _commands = commands;
        _context = context;
        _audit = audit;
    }

    public async Task<DispatchCommandResponse> BrowseAsync(
        Guid clientId, BrowseClientPathRequest request, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new NotFoundException("客户端", clientId);

        if (client.Status == ClientStatus.PendingApproval)
            throw new BusinessException("CONFLICT", "客户端尚未审批通过，不能浏览其目录", 409);
        if (client.Status is ClientStatus.Disabled or ClientStatus.Revoked)
            throw new BusinessException("CONFLICT", "客户端已禁用或已注销，不能浏览其目录", 409);

        var command = await _commands.CreateCommandAsync(
            clientId,
            CommandType.BrowsePath,
            payload: new BrowsePathCommandPayload
            {
                Path = string.IsNullOrWhiteSpace(request.Path) ? null : request.Path.Trim(),
                MaxDepth = Math.Clamp(request.MaxDepth, 1, 6),
                MaxEntries = Math.Clamp(request.MaxEntries, 50, 20000)
            },
            priority: 10,
            ttl: BrowseTtl,
            createdBy: _context.UserId,
            ct: ct);

        // 这是一条"从管理界面枚举客户端文件名"的能力。它有正当用途，但必须留痕：
        // 谁、在哪台机器上、看了哪个路径。审计不是形式，是这个能力可被接受的前提。
        await _audit.RecordAsync(
            "client.browse_path", AuditResult.Success, "client", clientId,
            afterData: JsonSerializer.Serialize(new { path = request.Path, commandId = command.Id }),
            ct: ct);

        return new DispatchCommandResponse { CommandId = command.Id };
    }

    public async Task<BrowseClientPathResultDto> GetBrowseResultAsync(Guid commandId, CancellationToken ct = default)
    {
        var command = await _db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commandId, ct)
            ?? throw new NotFoundException("指令", commandId);

        if (command.CommandType != CommandType.BrowsePath)
            throw new BusinessException("INVALID_REQUEST", "该指令不是目录浏览指令", 400);

        return new BrowseClientPathResultDto
        {
            CommandId = command.Id,
            Status = EnumMapping.ToSnakeCase(command.Status),
            CreatedAt = command.CreatedAt,
            CompletedAt = command.CompletedAt,
            ResultCode = command.ResultCode,
            ResultMessage = command.ResultMessage,
            Snapshot = command.Status == CommandStatus.Succeeded ? ParseSnapshot(command.ResultPayload) : null
        };
    }

    public async Task<RecognizerProposalDto> InferAsync(InferRecognizerRequest request, CancellationToken ct = default)
    {
        var (snapshot, source) = await ResolveScopeAsync(request, ct);
        var proposal = StructureInference.Infer(source, snapshot.CapturedAt);

        // 推断出来的规则立刻在同一份快照上跑一遍。
        // 只给结论不给结果，人没有办法判断这条规则对不对——而他一眼就能认出
        // "ZT007 只有一个文件"这种事实是不是符合他的预期。
        // 推断出来的规则是针对"展开之后的那个目录"的（数字分层会产出带通配的源路径），
        // 所以预演也必须跑在展开之后的节点上。跑在 Backup 上会得出"没有备份"，
        // 而那正是向导最不该撒的那种谎——规则是对的，只是预演找错了目录。
        var proposedRules = RecognizerRules.Parse(proposal.RecognizerConfig);
        var previewSource = source;
        if (WildcardPath.ContainsWildcard(proposal.SourcePath))
        {
            var (expanded, _) = SnapshotRecognizer.ExpandSource(source, proposal.SourcePath, proposedRules);
            if (expanded is not null)
                previewSource = expanded;
        }

        proposal.Preview = SnapshotRecognizer.Preview(
            previewSource,
            proposal.RecognizerType,
            proposedRules,
            DefaultStabilityIntervalSeconds,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);

        return proposal;
    }

    public async Task<RecognizerPreviewDto> PreviewAsync(PreviewRecognizerRequest request, CancellationToken ct = default)
    {
        if (!EnumMapping.TryParseSnakeCase<RecognizerType>(request.RecognizerType, out _))
            throw new BusinessException("INVALID_REQUEST", $"无效的识别类型：{request.RecognizerType}", 400);

        if (!string.IsNullOrWhiteSpace(request.RecognizerConfig))
        {
            try
            {
                using var _ = JsonDocument.Parse(request.RecognizerConfig);
            }
            catch (JsonException)
            {
                throw new BusinessException("INVALID_REQUEST", "识别规则配置不是合法的 JSON", 400);
            }
        }

        var rules = RecognizerRules.Parse(request.RecognizerConfig);
        var (snapshot, source) = await ResolveScopeAsync(request, ct, rules);
        return SnapshotRecognizer.Preview(
            source,
            request.RecognizerType,
            rules,
            request.StabilityIntervalSeconds,
            snapshot.CapturedAt,
            snapshot.DeniedPaths,
            snapshot.Truncated);
    }

    /// <summary>取出快照并定位到请求指定的目录。</summary>
    private async Task<(BrowseSnapshotDto Snapshot, SnapshotNode Source)> ResolveScopeAsync(
        SnapshotScopedRequest request, CancellationToken ct, RecognizerRules? rules = null)
    {
        var command = await _db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CommandId, ct)
            ?? throw new NotFoundException("指令", request.CommandId);

        if (command.CommandType != CommandType.BrowsePath)
            throw new BusinessException("INVALID_REQUEST", "该指令不是目录浏览指令", 400);
        if (command.Status != CommandStatus.Succeeded)
            throw new BusinessException("CONFLICT", "目录浏览尚未完成，无法据此推断", 409);

        var snapshot = ParseSnapshot(command.ResultPayload)
            ?? throw new BusinessException("CONFLICT", "目录浏览结果无法解析", 409);
        if (snapshot.IsDriveList)
            throw new BusinessException("INVALID_REQUEST", "请先打开一个具体目录，再进行识别推断", 400);

        var root = SnapshotNode.FromSnapshot(snapshot);
        if (string.IsNullOrWhiteSpace(request.Path))
            return (snapshot, root);

        // 源路径允许写 *（D:\Seeyon\A6\Backup\*\*）。展开走的是与 Agent 共用的
        // WildcardPath，否则预演会定位到与实际扫描不同的目录。
        var (expanded, failure) = SnapshotRecognizer.ExpandSource(root, request.Path, rules ?? RecognizerRules.Empty());
        if (failure is not null)
            throw new BusinessException("INVALID_REQUEST", failure, 400);

        var source = expanded
            ?? throw new BusinessException("INVALID_REQUEST", $"该路径不在本次浏览的范围内：{request.Path}", 400);
        if (!source.IsDirectory)
            throw new BusinessException("INVALID_REQUEST", "识别推断需要指向一个目录，而不是文件", 400);

        return (snapshot, source);
    }

    private static BrowseSnapshotDto? ParseSnapshot(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            return JsonSerializer.Deserialize<BrowseSnapshotDto>(payload, SnapshotJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
