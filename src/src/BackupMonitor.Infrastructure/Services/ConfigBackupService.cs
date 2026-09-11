using System.Diagnostics;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>
/// 配置备份包（.bmbp）的远程入口（整改清单 2026-09-10 · R4）。
///
/// 在此之前唯一的导出入口是服务管理台上的按钮——远程管理的人必须坐到服务器前面点，
/// 于是这件「整个系统的命」的事，实际执行频率取决于有没有人正好去过机房。
/// </summary>
public interface IConfigBackupService
{
    /// <summary>配置备份包现状：最近一份是什么时候的、有没有超期。</summary>
    Task<ConfigBackupStatusDto> GetStatusAsync(CancellationToken ct = default);

    /// <summary>立即导出一份配置备份包，落到数据目录旁的固定子目录。</summary>
    Task<ConfigBackupExportDto> ExportAsync(CancellationToken ct = default);

    /// <summary>为最近一份配置备份包签发一次性下载令牌。</summary>
    Task<ConfigBackupDownloadTokenDto> IssueDownloadTokenAsync(CancellationToken ct = default);

    /// <summary>核销下载令牌，返回待发送的文件路径与文件名。</summary>
    Task<(string FilePath, string FileName, long SizeBytes)> ConsumeDownloadTokenAsync(
        string token, CancellationToken ct = default);
}

public class ConfigBackupService : IConfigBackupService
{
    /// <summary>超期天数的 system_settings 键。</summary>
    public const string OverdueDaysKey = "config_backup_overdue_days";

    /// <summary>默认超期天数。手动导出模式下没有定时任务替人记着这件事，30 天是兜底而不是节奏。</summary>
    public const int DefaultOverdueDays = 30;

    /// <summary>
    /// 导出子进程的超时。pg_dump 的对象是元数据库（备份文件本体不在包里），
    /// 正常是秒级；给到 30 分钟是为了极端情况下不要在一台慢机器上误杀一次真在跑的导出——
    /// 杀掉留下的是半份包，比等着更糟。
    /// </summary>
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _db;
    private readonly SystemSettingsProvider _settings;
    private readonly IConfiguration _configuration;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<ConfigBackupService> _logger;

    public ConfigBackupService(
        AppDbContext db,
        SystemSettingsProvider settings,
        IConfiguration configuration,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<ConfigBackupService> logger)
    {
        _db = db;
        _settings = settings;
        _configuration = configuration;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ConfigBackupStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var directory = ResolveExportDirectory();
        var overdueDays = await GetOverdueDaysAsync(ct);
        var packages = EnumeratePackages(directory);
        var latest = packages.FirstOrDefault();

        var lastRemote = await _db.ConfigBackupExports.AsNoTracking()
            .OrderByDescending(e => e.StartedAt)
            .Select(e => new ConfigBackupExportDto
            {
                Id = e.Id,
                Status = EnumMapping.ToSnakeCase(e.Status),
                FileName = e.FileName,
                SizeBytes = e.SizeBytes,
                ErrorMessage = e.ErrorMessage,
                StartedAt = e.StartedAt,
                CompletedAt = e.CompletedAt,
                RequestedByName = e.RequestedByName
            })
            .FirstOrDefaultAsync(ct);

        var ageDays = latest is null
            ? (int?)null
            : Math.Max(0, (int)(DateTime.UtcNow - latest.ExportedAtUtc).TotalDays);

        return new ConfigBackupStatusDto
        {
            Directory = directory,
            LatestFileName = latest?.FileName,
            LatestExportedAt = latest?.ExportedAtUtc,
            LatestSizeBytes = latest?.SizeBytes,
            AgeDays = ageDays,
            PackageCount = packages.Count,
            OverdueDays = overdueDays,
            // 从未导出过也算超期：那恰恰是最该被看见的状态，
            // 「没有」不该因为算不出天数而显示成「正常」。
            Overdue = ageDays is null || ageDays > overdueDays
        };
    }

    public async Task<ConfigBackupExportDto> ExportAsync(CancellationToken ct = default)
    {
        var installer = ResolveSetupExecutable();
        var directory = ResolveExportDirectory();
        // 这里只建目录，不显式收 ACL：它是数据目录的子目录，继承的就是安装时收紧过的
        // SYSTEM + Administrators。包文件本身的 ACL 由导出进程
        //（ServerMaintenance.ExportBackupPackageAsync 的 SecureFileSystem.ApplyFileAcl）负责，
        // 那才是真正装着 CA 私钥的东西。
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, ConfigBackupPaths.BuildFileName(DateTime.Now));

        var record = new ConfigBackupExport
        {
            Id = Guid.NewGuid(),
            Status = ConfigBackupExportStatus.Running,
            FilePath = targetPath,
            FileName = Path.GetFileName(targetPath),
            StartedAt = DateTime.UtcNow,
            RequestedBy = _context.UserId,
            RequestedByName = _context.Username
        };
        _db.ConfigBackupExports.Add(record);
        await _db.SaveChangesAsync(ct);

        try
        {
            var (exitCode, stderr) = await RunExportProcessAsync(installer, targetPath, ct);
            if (exitCode != 0 || !File.Exists(targetPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"导出进程退出码 {exitCode}"
                    : stderr.Trim();
                throw new BusinessException("CONFIG_BACKUP_FAILED", $"配置备份包导出失败：{detail}", 500);
            }

            record.Status = ConfigBackupExportStatus.Succeeded;
            record.SizeBytes = new FileInfo(targetPath).Length;
            record.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.RecordAsync("config_backup.export", AuditResult.Success,
                "config_backup_export", record.Id, ct: ct);
        }
        catch (Exception ex)
        {
            record.Status = ConfigBackupExportStatus.Failed;
            record.ErrorMessage = ex.Message;
            record.CompletedAt = DateTime.UtcNow;
            // 半份包留在目录里会被下一次「下载最近一份」当成好包取走，必须清掉。
            TryDelete(targetPath);
            record.FilePath = null;
            record.FileName = null;
            await _db.SaveChangesAsync(CancellationToken.None);

            await _audit.RecordAsync("config_backup.export", AuditResult.Failure,
                "config_backup_export", record.Id, errorMessage: ex.Message, ct: CancellationToken.None);
            _logger.LogError(ex, "配置备份包导出失败");
            throw;
        }

        return new ConfigBackupExportDto
        {
            Id = record.Id,
            Status = EnumMapping.ToSnakeCase(record.Status),
            FileName = record.FileName,
            SizeBytes = record.SizeBytes,
            StartedAt = record.StartedAt,
            CompletedAt = record.CompletedAt,
            RequestedByName = record.RequestedByName
        };
    }

    public async Task<ConfigBackupDownloadTokenDto> IssueDownloadTokenAsync(CancellationToken ct = default)
    {
        var directory = ResolveExportDirectory();
        var latest = EnumeratePackages(directory).FirstOrDefault()
            ?? throw new BusinessException("NOT_FOUND",
                "还没有任何配置备份包可供下载，请先导出一份", 404);

        var ttlMinutes = await _settings.GetIntAsync("download_token_ttl_minutes", 30, ct);
        var token = TokenHasher.GenerateToken(32);

        // 令牌挂在一行导出记录上。最近一份可能是服务管理台本机导出的，
        // 那种包在这张表里没有对应行——补一行「已完成」的记录挂令牌，
        // 而不是为了下载去伪造一次导出。
        var record = await _db.ConfigBackupExports
            .Where(e => e.FilePath == latest.FullPath)
            .OrderByDescending(e => e.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (record is null)
        {
            record = new ConfigBackupExport
            {
                Id = Guid.NewGuid(),
                Status = ConfigBackupExportStatus.Succeeded,
                FilePath = latest.FullPath,
                FileName = latest.FileName,
                SizeBytes = latest.SizeBytes,
                StartedAt = latest.ExportedAtUtc,
                CompletedAt = latest.ExportedAtUtc,
                RequestedByName = "服务管理台"
            };
            _db.ConfigBackupExports.Add(record);
        }

        // 同一时刻只允许一个有效令牌：签发即作废上一个。
        await _db.ConfigBackupExports
            .Where(e => e.DownloadTokenHash != null && e.Id != record.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.DownloadTokenHash, (string?)null)
                .SetProperty(e => e.DownloadExpiresAt, (DateTime?)null), ct);

        record.DownloadTokenHash = TokenHasher.Sha256Hex(token);
        record.DownloadExpiresAt = DateTime.UtcNow.AddMinutes(ttlMinutes);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("config_backup.issue_token", AuditResult.Success,
            "config_backup_export", record.Id, ct: ct);

        return new ConfigBackupDownloadTokenDto
        {
            FileName = latest.FileName,
            SizeBytes = latest.SizeBytes,
            DownloadToken = token,
            DownloadUrl = $"/api/v1/config-backup-downloads/{token}",
            ExpiresAt = record.DownloadExpiresAt!.Value
        };
    }

    public async Task<(string FilePath, string FileName, long SizeBytes)> ConsumeDownloadTokenAsync(
        string token, CancellationToken ct = default)
    {
        var tokenHash = TokenHasher.Sha256Hex(token);
        var record = await _db.ConfigBackupExports
            .FirstOrDefaultAsync(e => e.DownloadTokenHash == tokenHash, ct)
            ?? throw new BusinessException("NOT_FOUND", "下载链接无效", 404);

        if (record.DownloadExpiresAt is null || record.DownloadExpiresAt <= DateTime.UtcNow)
            throw new BusinessException("CONFLICT", "下载链接已过期，请重新签发令牌", 409);

        if (string.IsNullOrWhiteSpace(record.FilePath) || !File.Exists(record.FilePath))
            throw new BusinessException("STORAGE_UNAVAILABLE", "配置备份包文件已不存在", 503);

        var info = new FileInfo(record.FilePath);

        // 一次性：核销在发送之前。包里有 CA 私钥——宁可让一次中断的下载需要重新签发令牌，
        // 也不能让一个链接被重复取用。
        record.DownloadTokenHash = null;
        record.DownloadExpiresAt = null;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("config_backup.download", AuditResult.Success,
            "config_backup_export", record.Id, ct: ct);

        return (info.FullName, record.FileName ?? info.Name, info.Length);
    }

    // ---------- 内部 ----------

    private sealed record PackageInfo(string FullPath, string FileName, long SizeBytes, DateTime ExportedAtUtc);

    /// <summary>目录里的配置备份包，最新的排在前面。目录不存在时返回空列表（= 从未导出过）。</summary>
    private static List<PackageInfo> EnumeratePackages(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles(ConfigBackupPaths.SearchPattern, SearchOption.TopDirectoryOnly)
                .Select(f => new PackageInfo(f.FullName, f.Name, f.Length, f.LastWriteTimeUtc))
                .OrderByDescending(p => p.ExportedAtUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string ResolveExportDirectory()
    {
        var dataDirectory = _configuration["Server:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new BusinessException("NOT_SUPPORTED",
                "当前部署形态没有服务端数据目录（Server:DataDirectory），配置备份包功能仅在一键部署形态下可用", 400);

        return ConfigBackupPaths.DirectoryFor(dataDirectory);
    }

    /// <summary>
    /// 服务管理台可执行文件的位置。安装时由 ServerInstaller.StageSetupExecutable 复制到安装目录，
    /// 也就是 API 自己所在的那个目录。
    ///
    /// 找不到时明确报错而不是退回「API 自己实现一份导出」：
    /// 两套导出格式迟早分家，而分家的后果是包导不回去，且要到真正恢复那天才发现。
    /// </summary>
    private static string ResolveSetupExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "BackupMonitor.Server.Setup.exe");
        if (!File.Exists(path))
            throw new BusinessException("NOT_SUPPORTED",
                "安装目录中找不到服务管理台程序，无法远程导出配置备份包；请用安装器执行一次「修复 / 升级安装」", 503);
        return path;
    }

    private static async Task<(int ExitCode, string StandardError)> RunExportProcessAsync(
        string installer, string targetPath, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = installer,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("--export-backup-package");
        process.StartInfo.ArgumentList.Add(targetPath);

        if (!process.Start())
            throw new BusinessException("CONFIG_BACKUP_FAILED", "无法启动配置备份包导出进程", 500);

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        // 不接管调用方的 ct 去杀进程：HTTP 请求断了不代表导出该半途而废，
        // 半份包比没有包更糟。只有超时才终止。
        using var timeout = new CancellationTokenSource(ExportTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new BusinessException("CONFIG_BACKUP_FAILED",
                $"配置备份包导出超过 {ExportTimeout.TotalMinutes:0} 分钟仍未完成，已终止", 504);
        }

        return (process.ExitCode, await stderrTask);
    }

    private async Task<int> GetOverdueDaysAsync(CancellationToken ct) =>
        Math.Clamp(await _settings.GetIntAsync(OverdueDaysKey, DefaultOverdueDays, ct), 1, 3650);

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
