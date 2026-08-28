using BackupMonitor.Api.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackupMonitor.Api.Controllers;

/// <summary>Agent 分块上传协议（设计书 14）</summary>
[Route("api/v1/agent/upload-sessions")]
[Authorize(AuthenticationSchemes = ClientCertificateAuthenticationHandler.SchemeName)]
public class AgentUploadController : ApiBaseController
{
    /// <summary>分块上传请求体上限（32MB 分块 + 余量）</summary>
    private const long MaxChunkBodyBytes = 34L * 1024 * 1024;

    private readonly IUploadSessionService _sessionService;

    public AgentUploadController(IUploadSessionService sessionService)
    {
        _sessionService = sessionService;
    }

    /// <summary>创建上传会话（14.1，幂等键支持断点续传）</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<CreateUploadSessionResponse>>> CreateSession(
        [FromBody] CreateUploadSessionRequest request, CancellationToken ct)
    {
        var result = await _sessionService.CreateSessionAsync(ClientIdentity, request, ct);
        return OkData(result);
    }

    /// <summary>查询文件缺失分块（14.2）</summary>
    [HttpGet("{sessionId:guid}/files/{fileId:guid}/missing-chunks")]
    public async Task<ActionResult<ApiResponse<MissingChunksResponse>>> MissingChunks(
        Guid sessionId, Guid fileId, CancellationToken ct)
    {
        var result = await _sessionService.GetMissingChunksAsync(ClientIdentity, sessionId, fileId, ct);
        return OkData(result);
    }

    /// <summary>上传分块（14.3，二进制流；块偏移由 X-Chunk-Offset 头携带）</summary>
    [HttpPut("{sessionId:guid}/files/{fileId:guid}/chunks/{chunkIndex:int}")]
    [RequestSizeLimit((int)MaxChunkBodyBytes)]
    public async Task<ActionResult<ApiResponse<UploadChunkResponse>>> UploadChunk(
        Guid sessionId, Guid fileId, int chunkIndex, CancellationToken ct)
    {
        if (!long.TryParse(Request.Headers["X-Chunk-Offset"].FirstOrDefault(), out var offset) || offset < 0)
            throw new BusinessException("INVALID_REQUEST", "缺少或非法的 X-Chunk-Offset 请求头", 400);

        var expectedHash = Request.Headers["X-Chunk-Sha256"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expectedHash))
            throw new BusinessException("INVALID_REQUEST", "缺少 X-Chunk-Sha256 请求头", 400);

        var result = await _sessionService.UploadChunkAsync(
            ClientIdentity, sessionId, fileId, chunkIndex, offset, expectedHash, Request.Body, ct);
        return OkData(result);
    }

    /// <summary>完成单文件（14.4，服务端整文件哈希比对）</summary>
    [HttpPost("{sessionId:guid}/files/{fileId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<CompleteUploadFileResponse>>> CompleteFile(
        Guid sessionId, Guid fileId, [FromBody] CompleteUploadFileRequest request, CancellationToken ct)
    {
        var result = await _sessionService.CompleteFileAsync(ClientIdentity, sessionId, fileId, request, ct);
        return OkData(result);
    }

    /// <summary>完成会话（14.5，进入异步校验入库）</summary>
    [HttpPost("{sessionId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<CompleteUploadSessionResponse>>> CompleteSession(
        Guid sessionId, [FromBody] CompleteUploadSessionRequest request, CancellationToken ct)
    {
        var result = await _sessionService.CompleteSessionAsync(ClientIdentity, sessionId, request, ct);
        return OkData(result);
    }

    /// <summary>查询会话状态（14.6，含各文件进度与缺失块）</summary>
    [HttpGet("{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<UploadSessionStatusResponse>>> GetSession(
        Guid sessionId, CancellationToken ct)
    {
        var result = await _sessionService.GetSessionStatusAsync(ClientIdentity, sessionId, ct);
        return OkData(result);
    }

    /// <summary>
    /// 中断会话（待办方案 E）：这次传失败了，但会话与暂存留着，下次从缺哪块传哪块接着传。
    /// Agent 此前遇到异常一律调 cancel，于是「断网能续传，出错不能续传」。
    /// </summary>
    [HttpPost("{sessionId:guid}/interrupt")]
    public async Task<ActionResult<ApiResponse>> InterruptSession(
        Guid sessionId, [FromBody] InterruptUploadSessionRequest request, CancellationToken ct)
    {
        await _sessionService.InterruptSessionAsync(ClientIdentity, sessionId, request, ct);
        return OkMessage("会话已标记为可续传");
    }

    /// <summary>取消会话（14.7，已入库会话禁止取消）</summary>
    [HttpPost("{sessionId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse>> CancelSession(Guid sessionId, CancellationToken ct)
    {
        await _sessionService.CancelSessionAsync(ClientIdentity, sessionId, ct);
        return OkMessage("会话已取消");
    }
}
