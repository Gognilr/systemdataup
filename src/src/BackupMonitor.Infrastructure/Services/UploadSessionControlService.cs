using System.Text.Json;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 管理端对一次传输的干预（待办方案 E）：暂停 / 恢复 / 取消。
///
/// 「传输中」页面此前是一张纯只读表格，一个按钮都没有——白天发现某台机器
/// 正在把带宽占满时，唯一能做的是等它传完。
///
/// 暂停的实现落在服务端：会话置 paused 之后就不在可写状态里，客户端再写分块一律 409。
/// 恢复则把会话放回可写并重新下发一条上传指令，客户端凭幂等键拿回同一个会话，
/// 从缺哪块传哪块继续——和断网续传走的是同一条路。
/// </summary>
public interface IUploadSessionControlService
{
    Task PauseAsync(Guid sessionId, CancellationToken ct = default);
    Task ResumeAsync(Guid sessionId, CancellationToken ct = default);
    Task CancelAsync(Guid sessionId, CancellationToken ct = default);
}

public class UploadSessionControlService : IUploadSessionControlService
{
    private static readonly UploadStatus[] PausableStatuses = UploadSessionStatuses.Pausable;
    private static readonly UploadStatus[] CancellableStatuses = UploadSessionStatuses.Cancellable;

    private readonly AppDbContext _db;
    private readonly ICommandDispatcher _commands;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<UploadSessionControlService> _logger;

    public UploadSessionControlService(
        AppDbContext db,
        ICommandDispatcher commands,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<UploadSessionControlService> logger)
    {
        _db = db;
        _commands = commands;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task PauseAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await Load(sessionId, ct);

        if (session.Status == UploadStatus.Paused)
            return;
        if (!PausableStatuses.Contains(session.Status))
            throw new BusinessException("CONFLICT",
                $"会话当前状态为 {EnumMapping.ToSnakeCase(session.Status)}，不能暂停", 409);

        session.Status = UploadStatus.Paused;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("upload.session.pause", AuditResult.Success, "upload_session", session.Id, ct: ct);
        _logger.LogInformation("上传会话 {SessionId} 已暂停（暂存与已传分块保留）", session.Id);
    }

    public async Task ResumeAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await Load(sessionId, ct);

        if (session.Status != UploadStatus.Paused)
            throw new BusinessException("CONFLICT", "只有暂停中的会话才能恢复", 409);

        // 传过东西的回 uploading，一块都还没传的回 created——两者都在可写状态里，
        // 区别只在界面上显示成「传输中」还是「等待开始」。
        session.Status = session.UploadedBytes > 0 ? UploadStatus.Uploading : UploadStatus.Created;
        session.UpdatedAt = DateTime.UtcNow;
        session.LastActivityAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // 重新下发上传指令：暂停期间客户端那条指令多半已经失败退出了，
        // 只把状态改回可写没人会来接着传。幂等键带上会话 ID，恢复两次也只有一条指令。
        await _commands.CreateCommandAsync(
            session.ClientId,
            CommandType.UploadCandidate,
            taskId: session.TaskId,
            candidateBackupSetId: session.CandidateBackupSetId,
            payload: new { candidateBackupSetId = session.CandidateBackupSetId, resumeSessionId = session.Id },
            idempotencyKey: $"resume:{session.Id}",
            createdBy: _context.UserId,
            restartIfNotActive: true,
            ct: ct);

        await _audit.RecordAsync("upload.session.resume", AuditResult.Success, "upload_session", session.Id,
            afterData: JsonSerializer.Serialize(new { session.UploadedBytes }), ct: ct);
        _logger.LogInformation("上传会话 {SessionId} 已恢复，已下发续传指令", session.Id);
    }

    public async Task CancelAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await Load(sessionId, ct);

        if (session.Status == UploadStatus.Cancelled)
            return;
        if (!CancellableStatuses.Contains(session.Status))
            throw new BusinessException("CONFLICT",
                $"会话当前状态为 {EnumMapping.ToSnakeCase(session.Status)}，不能取消", 409);

        session.Status = UploadStatus.Cancelled;
        session.CompletedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("upload.session.cancel", AuditResult.Success, "upload_session", session.Id,
            afterData: JsonSerializer.Serialize(new { by = "admin" }), ct: ct);
        _logger.LogInformation("上传会话 {SessionId} 已被管理员取消", session.Id);
    }

    private async Task<Core.Entities.Upload.UploadSession> Load(Guid sessionId, CancellationToken ct) =>
        await _db.UploadSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new NotFoundException("上传会话", sessionId);
}
