using System.Security.Cryptography;
using BackupMonitor.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 上传暂存物理存储（设计书 23.5 文件安全 / 24 上传状态一致性）：
/// 临时目录与正式仓库分离，分块随机写入偏移，哈希双端校验。
/// </summary>
public interface IUploadStorage
{
    /// <summary>暂存根目录（优先 system_settings.staging_path）</summary>
    Task<string> GetStagingRootAsync(CancellationToken ct = default);

    /// <summary>正式仓库根目录（优先 system_settings.repository_path）</summary>
    Task<string> GetRepositoryRootAsync(CancellationToken ct = default);

    /// <summary>会话暂存目录</summary>
    Task<string> GetSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>确保会话目录存在，返回目录路径</summary>
    Task<string> EnsureSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>暂存盘剩余空间（字节）</summary>
    Task<long> GetStagingFreeBytesAsync(CancellationToken ct = default);

    /// <summary>将分块写入暂存文件指定偏移，返回服务端计算的块哈希（十六进制小写）与本次实际写入字节数</summary>
    Task<(string Hash, long BytesWritten)> WriteChunkAsync(Guid sessionId, Guid uploadFileId, int chunkIndex, long offset, Stream data, CancellationToken ct = default);

    /// <summary>计算暂存文件整体 SHA-256（十六进制小写）</summary>
    Task<string> ComputeFileHashAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default);

    /// <summary>暂存文件是否存在及长度</summary>
    Task<(bool Exists, long Length)> GetStagedFileInfoAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default);

    /// <summary>读取暂存文件流（提交入库用）</summary>
    Task<Stream> OpenStagedFileAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default);

    /// <summary>尽力清理会话暂存目录（失败仅记日志）</summary>
    Task CleanupSessionAsync(Guid sessionId, CancellationToken ct = default);
}

/// <summary>上传暂存实现</summary>
public class UploadStorage : IUploadStorage
{
    private readonly SystemSettingsProvider _settings;
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _db;
    private readonly ILogger<UploadStorage> _logger;

    public UploadStorage(
        SystemSettingsProvider settings,
        IConfiguration configuration,
        AppDbContext db,
        ILogger<UploadStorage> logger)
    {
        _settings = settings;
        _configuration = configuration;
        _db = db;
        _logger = logger;
    }

    public async Task<string> GetStagingRootAsync(CancellationToken ct = default)
    {
        var fromDb = await _settings.GetStringAsync("staging_path", ct);
        var root = !string.IsNullOrWhiteSpace(fromDb)
            ? fromDb
            : _configuration["Storage:StagingPath"]
              ?? Path.Combine(AppContext.BaseDirectory, "data", "staging");
        Directory.CreateDirectory(root);
        return root;
    }

    public async Task<string> GetRepositoryRootAsync(CancellationToken ct = default)
    {
        var fromDb = await _settings.GetStringAsync("repository_path", ct);
        var root = !string.IsNullOrWhiteSpace(fromDb)
            ? fromDb
            : _configuration["Storage:RepositoryPath"]
              ?? Path.Combine(AppContext.BaseDirectory, "data", "repository");
        Directory.CreateDirectory(root);
        return root;
    }

    public async Task<string> GetSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default)
    {
        var root = await GetStagingRootAsync(ct);
        return Path.Combine(root, "sessions", sessionId.ToString("N"));
    }

    public async Task<string> EnsureSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default)
    {
        var dir = await GetSessionDirectoryAsync(sessionId, ct);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task<long> GetStagingFreeBytesAsync(CancellationToken ct = default)
    {
        var root = await GetStagingRootAsync(ct);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            return drive.IsReady ? drive.AvailableFreeSpace : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法获取暂存盘剩余空间 root={Root}", root);
            return long.MaxValue;
        }
    }

    public async Task<(string Hash, long BytesWritten)> WriteChunkAsync(
        Guid sessionId, Guid uploadFileId, int chunkIndex, long offset, Stream data, CancellationToken ct = default)
    {
        var dir = await EnsureSessionDirectoryAsync(sessionId, ct);
        var path = Path.Combine(dir, $"{uploadFileId:N}.part");

        await using var fileStream = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            bufferSize: 81920, useAsync: false);
        fileStream.Seek(offset, SeekOrigin.Begin);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long bytesWritten = 0;
        int read;
        while ((read = await data.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesWritten += read;
        }

        await fileStream.FlushAsync(ct);
        return (Convert.ToHexString(sha.GetCurrentHash()).ToLowerInvariant(), bytesWritten);
    }

    public async Task<string> ComputeFileHashAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default)
    {
        var dir = await GetSessionDirectoryAsync(sessionId, ct);
        var path = Path.Combine(dir, $"{uploadFileId:N}.part");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<(bool Exists, long Length)> GetStagedFileInfoAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default)
    {
        var dir = await GetSessionDirectoryAsync(sessionId, ct);
        var info = new FileInfo(Path.Combine(dir, $"{uploadFileId:N}.part"));
        return info.Exists ? (true, info.Length) : (false, 0);
    }

    public async Task<Stream> OpenStagedFileAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default)
    {
        var dir = await GetSessionDirectoryAsync(sessionId, ct);
        var path = Path.Combine(dir, $"{uploadFileId:N}.part");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    public async Task CleanupSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            var dir = await GetSessionDirectoryAsync(sessionId, ct);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理会话暂存目录失败 sessionId={SessionId}（将由定时清理兜底）", sessionId);
        }
    }
}
