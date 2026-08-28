using System.Buffers;
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

    /// <summary>
    /// 解析某个存储根的取值与来源，不创建目录。
    /// 管理端「存储设置」用它回答两个问题：现在实际用的是哪个目录，这个值是谁给的。
    /// </summary>
    Task<StorageRootResolution> ResolveRootAsync(string settingKey, CancellationToken ct = default);

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

    /// <summary>
    /// 暂存文件的绝对路径。入库时若暂存与仓库在同一个 NTFS 卷上，
    /// 可以直接建硬链接而不是整份复制（见 UploadCommitWorker），因此需要路径而不只是流。
    /// </summary>
    Task<string> GetStagedFilePathAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default);

    /// <summary>尽力清理会话暂存目录（失败仅记日志）</summary>
    Task CleanupSessionAsync(Guid sessionId, CancellationToken ct = default);
}

/// <summary>存储根的解析结果：最终路径 + 这个值是谁给的</summary>
/// <param name="Path">解析出的绝对路径（未创建目录）</param>
/// <param name="Source">database / configuration / default，见 <see cref="StorageRootSource"/></param>
public readonly record struct StorageRootResolution(string Path, string Source);

/// <summary>存储根取值来源</summary>
public static class StorageRootSource
{
    /// <summary>system_settings 表里管理员配置的值</summary>
    public const string Database = "database";

    /// <summary>appsettings 的 Storage:* 配置项</summary>
    public const string Configuration = "configuration";

    /// <summary>都没配时的兜底：程序目录下的 data\repository|staging</summary>
    public const string Default = "default";
}

/// <summary>上传暂存实现</summary>
public class UploadStorage : IUploadStorage
{
    /// <summary>正式仓库根在 system_settings 中的键</summary>
    public const string RepositorySettingKey = "repository_path";

    /// <summary>暂存根在 system_settings 中的键</summary>
    public const string StagingSettingKey = "staging_path";

    /// <summary>两个存储根各自的回退链定义：appsettings 配置键 + 兜底子目录名</summary>
    private static readonly Dictionary<string, (string ConfigKey, string DefaultSubdirectory)> RootDefinitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [RepositorySettingKey] = ("Storage:RepositoryPath", "repository"),
            [StagingSettingKey] = ("Storage:StagingPath", "staging")
        };

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

    /// <summary>
    /// 解析存储根：system_settings → appsettings 的 Storage:* → 程序目录下的 data\&lt;子目录&gt;。
    ///
    /// 三层都用 IsNullOrWhiteSpace 判空，不用 ??。appsettings.json 里 Storage:StagingPath
    /// 的出厂值是空串而不是缺省键，`_configuration[...] ?? 默认值` 取到的是空串本身，
    /// 于是 Directory.CreateDirectory("") 抛 ArgumentException——库里没配路径的部署
    /// （V014 之后就是这个状态）任何一次仓库解析都会炸，且错误信息跟路径毫无关系。
    /// </summary>
    public async Task<StorageRootResolution> ResolveRootAsync(string settingKey, CancellationToken ct = default)
    {
        if (!RootDefinitions.TryGetValue(settingKey, out var definition))
            throw new ArgumentOutOfRangeException(nameof(settingKey), settingKey, "未知的存储根配置键");

        var fromDb = await _settings.GetStringAsync(settingKey, ct);
        if (!string.IsNullOrWhiteSpace(fromDb))
            return new StorageRootResolution(fromDb.Trim(), StorageRootSource.Database);

        var fromConfig = _configuration[definition.ConfigKey];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return new StorageRootResolution(fromConfig.Trim(), StorageRootSource.Configuration);

        return new StorageRootResolution(
            Path.Combine(AppContext.BaseDirectory, "data", definition.DefaultSubdirectory),
            StorageRootSource.Default);
    }

    public async Task<string> GetStagingRootAsync(CancellationToken ct = default)
    {
        var root = (await ResolveRootAsync(StagingSettingKey, ct)).Path;
        Directory.CreateDirectory(root);
        return root;
    }

    public async Task<string> GetRepositoryRootAsync(CancellationToken ct = default)
    {
        var root = (await ResolveRootAsync(RepositorySettingKey, ct)).Path;
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
            // 审查 P1-8：这是安全相关判断，必须 fail-closed。
            // 原先返回 long.MaxValue 会让 CreateSessionAsync 的空间预检恒定通过，
            // 上传照常开始、直到写盘才崩；而同一方法在「驱动器未就绪」时返回 0，
            // 两条失败路径给出的答案完全相反。统一为 0，由调用方以 STORAGE_SPACE_LOW 明确拒绝。
            _logger.LogWarning(ex, "无法获取暂存盘剩余空间 root={Root}，按 0 处理以拒绝新会话", root);
            return 0;
        }
    }

    /// <summary>
    /// 分块读写缓冲大小。
    ///
    /// 80KB 是 .NET 的历史默认值，对 4–32MB 的分块意味着每块上百次读写往返。
    /// 1MB 在实测里把纯写吞吐从 437MB/s 抬到 686MB/s，而分块下限是 4MB，
    /// 一块至少还能填满四个缓冲，不存在「缓冲比数据还大」的浪费。
    /// </summary>
    private const int ChunkIoBufferBytes = 1024 * 1024;

    public async Task<(string Hash, long BytesWritten)> WriteChunkAsync(
        Guid sessionId, Guid uploadFileId, int chunkIndex, long offset, Stream data, CancellationToken ct = default)
    {
        var dir = await EnsureSessionDirectoryAsync(sessionId, ct);
        var path = Path.Combine(dir, $"{uploadFileId:N}.part");

        // 审查 P2-10：断点续传协议本就允许同一文件的多个分块并行上传，
        // 原先以 FileShare.Read 打开，第二个并发写入直接抛 IOException；
        // 且 useAsync:false 配合 WriteAsync 会阻塞线程池线程。
        // 各分块写入区间由 offset 严格划分且互不重叠，允许并发写是安全的。
        await using var fileStream = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite,
            bufferSize: ChunkIoBufferBytes, FileOptions.Asynchronous);
        fileStream.Seek(offset, SeekOrigin.Begin);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkIoBufferBytes);
        long bytesWritten = 0;
        try
        {
            int read;
            // Rent 可能给回更大的数组，只用申请的那一段，避免把池里的旧字节算进哈希。
            while ((read = await data.ReadAsync(buffer.AsMemory(0, ChunkIoBufferBytes), ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                bytesWritten += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // 审查 P1-6：分块被应答为「已接收」时必须真正落盘，否则断电后
        // 客户端认为已传完、服务端却少了数据。这条要求不变，变的是达成方式。
        //
        // 原先用 FileOptions.WriteThrough，代价是**每一次 Write 都是一次持久化写**：
        // 8MB 分块 / 80KB 缓冲 = 每块 100 次。裸盘上单次 0.3–3ms 还能忍，
        // 但在虚拟磁盘（PVE/ESXi）、ZFS 无 SLOG、Ceph 这类环境里单次能到 15–30ms，
        // 乘以 100 就是 1.5–3 秒/块——于是整条链路的速度由存储的同步写延迟决定，
        // 而不是由网络或数据量决定。现场表现就是千兆内网只跑出 3.7MB/s。
        //
        // 改成「正常缓冲写 + 收尾一次 FlushFileBuffers」：落盘保证完全等价
        // （应答发出前数据已在盘上），但持久化操作从每块 100 次降到 1 次。
        // 慢存储的单次延迟仍然存在，只是不再被乘以 100——这才是环境无关的写法。
        //
        // 只能同步调用：FlushAsync 不做 flushToDisk，.NET 没有异步版 FlushFileBuffers。
        // 每块一次，代价可接受；同文件并发写时各 handle 刷的是同一个文件，是安全的超集。
        fileStream.Flush(flushToDisk: true);

        return (Convert.ToHexString(sha.GetCurrentHash()).ToLowerInvariant(), bytesWritten);
    }

    public async Task<string> ComputeFileHashAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default)
    {
        var dir = await GetSessionDirectoryAsync(sessionId, ct);
        var path = Path.Combine(dir, $"{uploadFileId:N}.part");

        // 整文件复核要顺序读完整个暂存文件（6GB 备份就是 6GB 读）。
        // 1MB 缓冲 + SequentialScan 让预读真正起作用，比 80KB 默认值快一大截。
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkIoBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
        // 入库时要把整份暂存文件顺序读出来复制进仓库，同样吃预读。
        return new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkIoBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task<string> GetStagedFilePathAsync(Guid sessionId, Guid uploadFileId, CancellationToken ct = default)
    {
        var dir = await GetSessionDirectoryAsync(sessionId, ct);
        return Path.Combine(dir, $"{uploadFileId:N}.part");
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
