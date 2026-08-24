using System.IO.Compression;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace BackupMonitor.Api.Controllers;

/// <summary>
/// 恢复下载端点（设计书 19.3）：令牌即凭据，无需 JWT。
/// 无 path 参数 → 整套备份集打包 ZIP 流式下载；
/// ?path=相对路径 → 单文件下载，支持 Range 断点续传。
/// 记录下载 IP 与字节数。
/// </summary>
[Route("api/v1/downloads")]
public class DownloadsController : ApiBaseController
{
    private readonly IRestoreService _restoreService;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(IRestoreService restoreService, ILogger<DownloadsController> logger)
    {
        _restoreService = restoreService;
        _logger = logger;
    }

    [HttpGet("{downloadToken}")]
    [AllowAnonymous]
    public async Task Get(string downloadToken, [FromQuery] string? path, CancellationToken ct)
    {
        // ZipArchive 在 Dispose 时同步写中央目录，Kestrel 默认禁止同步 IO，这里按请求放行
        var bodyControl = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (bodyControl is not null)
            bodyControl.AllowSynchronousIO = true;

        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var context = await _restoreService.BeginDownloadAsync(downloadToken, clientIp, ct);

        if (string.IsNullOrWhiteSpace(path))
            await StreamZipAsync(context, ct);
        else
            await StreamSingleFileAsync(context, path, ct);
    }

    /// <summary>整套备份集打包 ZIP 流式输出（分块传输，无 Content-Length）</summary>
    private async Task StreamZipAsync(RestoreDownloadContext context, CancellationToken ct)
    {
        var counting = new CountingStream(Response.Body);
        var completed = false;
        try
        {
            // 预检全部文件：此时尚未写出响应头，错误仍以统一 JSON 形状返回；
            // 放在 try 内保证失败时也执行下载记账，请求不会无声卡在 downloading
            foreach (var file in context.Files)
            {
                // OPEN-ISSUES #8：zip-slip 纵深防御——条目名必须是规范化相对路径。
                // 入库时已过 PathSafety 校验，此处是交给下游解压方之前的服务端复检。
                if (!PathSafety.IsValidRelativePath(file.RelativePath))
                    throw new BusinessException("INVALID_REQUEST", $"非法的归档条目名：{file.RelativePath}", 400);

                RequireFileExists(context.RepositoryPath, file);
            }

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/zip";
            Response.Headers.ContentDisposition = BuildContentDisposition($"{context.BackupSetCode}.zip");

            using (var zip = new ZipArchive(counting, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in context.Files)
                {
                    var fullPath = ResolveFile(context.RepositoryPath, file);
                    // ZIP 条目名统一正斜杠（跨平台解压兼容）
                    var entry = zip.CreateEntry(file.RelativePath.Replace('\\', '/'), CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await fileStream.CopyToAsync(entryStream, ct);
                }
            } // using 结束时写入中央目录

            completed = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("恢复下载中断（客户端断开）request={RequestId} 已传输 {Bytes} 字节", context.RequestId, counting.BytesWritten);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复下载失败 request={RequestId}", context.RequestId);
            // 尚未开始写出时重新抛出，让统一异常中间件返回标准 JSON 错误
            if (!Response.HasStarted)
                throw;
        }
        finally
        {
            // 整包 ZIP 一次交付全部文件，completed 即代表整个请求完成
            await _restoreService.FinishDownloadAsync(
                context.RequestId, counting.BytesWritten, completed, deliveredRelativePath: null, CancellationToken.None);
        }
    }

    /// <summary>单文件下载，支持 Range（bytes=start-end）</summary>
    private async Task StreamSingleFileAsync(RestoreDownloadContext context, string path, CancellationToken ct)
    {
        var file = context.Files.FirstOrDefault(f => string.Equals(f.RelativePath, path, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException("备份文件", path);

        var fullPath = ResolveFile(context.RepositoryPath, file);
        if (!System.IO.File.Exists(fullPath))
            throw new BusinessException("STORAGE_UNAVAILABLE", $"仓库中文件缺失：{file.RelativePath}", 503);

        var fileLength = new FileInfo(fullPath).Length;

        long start = 0;
        var end = fileLength - 1;
        var isRangeRequest = false;

        if (Request.Headers.TryGetValue(HeaderNames.Range, out var rangeRaw)
            && RangeHeaderValue.TryParse(rangeRaw.FirstOrDefault(), out var range)
            && range is not null
            && range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            && range.Ranges.Count == 1)
        {
            var single = range.Ranges.First();
            if (single.From is null && single.To is null)
            {
                RespondRangeNotSatisfiable(fileLength);
                return;
            }

            if (single.From is null)
            {
                // bytes=-N：最后 N 个字节
                start = Math.Max(0, fileLength - single.To!.Value);
                end = fileLength - 1;
            }
            else
            {
                start = single.From.Value;
                end = single.To is null ? fileLength - 1 : Math.Min(single.To.Value, fileLength - 1);
            }

            if (start >= fileLength || start < 0 || end < start)
            {
                RespondRangeNotSatisfiable(fileLength);
                return;
            }

            isRangeRequest = true;
        }

        var length = end - start + 1;

        Response.StatusCode = isRangeRequest ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition = BuildContentDisposition(file.FileName);
        Response.ContentLength = length;
        if (isRangeRequest)
            Response.Headers.ContentRange = $"bytes {start}-{end}/{fileLength}";

        long copied = 0;
        var fileFullyDelivered = false;
        try
        {
            await using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            fileStream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[81920];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await fileStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0)
                    break;
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                remaining -= read;
            }

            // 审查 P1-7：这里只能断言「本文件是否完整交付」（含从 0 开始的 Range 全量请求）。
            // 整个恢复请求是否完成，由 RestoreService 按已交付文件集合判定——
            // 原先直接把它当作整个请求的完成标志，取走一个文件就 Completed。
            fileFullyDelivered = start == 0 && copied == fileLength;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("恢复下载中断（客户端断开）request={RequestId} 已传输 {Bytes} 字节", context.RequestId, copied);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复下载失败 request={RequestId} file={File}", context.RequestId, file.RelativePath);
            // 审查 P1-7：与 ZIP 分支对齐——尚未开始写出时重抛，让统一异常中间件
            // 返回标准 JSON 错误。原先这里吞掉异常，客户端会拿到一个空 200。
            if (!Response.HasStarted)
                throw;
        }
        finally
        {
            await _restoreService.FinishDownloadAsync(
                context.RequestId, copied,
                wholeSetDelivered: false,
                deliveredRelativePath: fileFullyDelivered ? file.RelativePath : null,
                CancellationToken.None);
        }
    }

    private void RespondRangeNotSatisfiable(long fileLength)
    {
        Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        Response.Headers.ContentRange = $"bytes */{fileLength}";
    }

    private static string ResolveFile(string repositoryPath, RestoreDownloadFileInfo file)
    {
        return PathSafety.ResolveUnderBase(repositoryPath, file.RepositoryRelativePath.Replace('/', Path.DirectorySeparatorChar))
            ?? throw new BusinessException("INVALID_REQUEST", $"非法文件路径：{file.RelativePath}", 400);
    }

    private static void RequireFileExists(string repositoryPath, RestoreDownloadFileInfo file)
    {
        var fullPath = ResolveFile(repositoryPath, file);
        if (!System.IO.File.Exists(fullPath))
            throw new BusinessException("STORAGE_UNAVAILABLE", $"仓库中文件缺失：{file.RelativePath}", 503);
    }

    /// <summary>Content-Disposition：ASCII 回退名 + RFC 5987 UTF-8 文件名</summary>
    private static string BuildContentDisposition(string fileName)
    {
        var ascii = new string(fileName.Select(ch => ch >= 32 && ch < 127 && ch != '"' && ch != '\\' ? ch : '_').ToArray());
        if (string.IsNullOrWhiteSpace(ascii))
            ascii = "download";
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    /// <summary>只计字节数的写出包装流</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
