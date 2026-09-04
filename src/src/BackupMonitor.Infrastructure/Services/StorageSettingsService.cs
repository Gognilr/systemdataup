using System.Globalization;
using System.Text.Json;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupMonitor.Infrastructure.Services;

/// <summary>存储路径设置的读写（system_settings 的 repository_path / staging_path）</summary>
public interface IStorageSettingsService
{
    /// <summary>读取当前设置，并实地探测两个目录的存在性/可写性/剩余空间</summary>
    Task<StorageSettingsDto> GetAsync(CancellationToken ct = default);

    /// <summary>更新设置。留空表示清除该项配置，回落到 appsettings 或程序目录默认值</summary>
    Task<StorageSettingsDto> UpdateAsync(UpdateStorageSettingsRequest request, CancellationToken ct = default);

    /// <summary>浏览服务端本机目录（path 为空时返回磁盘列表）。只列目录，不列文件</summary>
    Task<StorageBrowseResponseDto> BrowseAsync(string? path, CancellationToken ct = default);
}

/// <summary>
/// 存储路径设置实现。
///
/// 写入前一律实地验证：建目录、写一个探针文件再删掉。只检查字符串格式是不够的——
/// 路径合法但盘符不存在、或服务账户对该目录没有写权限，都要等到下一次真有备份要落盘
/// 才暴露，而那时失败的是一次真实备份。
/// </summary>
public class StorageSettingsService : IStorageSettingsService
{
    private const int MaxPathLength = 2048;

    /// <summary>单次目录浏览返回的子目录上限。超出就截断并告诉界面，不做分页——
    /// 存储根一般选在层级很浅的地方，需要翻两千个同级目录才能找到的场景不存在。</summary>
    private const int MaxBrowseEntries = 2000;

    private readonly AppDbContext _db;
    private readonly IUploadStorage _storage;
    private readonly SystemSettingsProvider _settings;
    private readonly ICurrentContext _context;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<StorageSettingsService> _logger;

    public StorageSettingsService(
        AppDbContext db,
        IUploadStorage storage,
        SystemSettingsProvider settings,
        ICurrentContext context,
        IAuditRecorder audit,
        ILogger<StorageSettingsService> logger)
    {
        _db = db;
        _storage = storage;
        _settings = settings;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    public async Task<StorageSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var repository = await DescribeAsync(UploadStorage.RepositorySettingKey, ct);
        var staging = await DescribeAsync(UploadStorage.StagingSettingKey, ct);

        return new StorageSettingsDto
        {
            Repository = repository,
            Staging = staging,
            ServerHostname = Environment.MachineName,
            BackupSetsOutsideRoot = await CountBackupSetsOutsideRootAsync(repository.EffectivePath, ct),
            MaxConcurrentUploadsTotal = await ReadUploadLimitAsync(ct),
            ActiveUploads = await _db.UploadSessions
                .CountAsync(u => UploadSessionStatuses.Active.Contains(u.Status), ct)
        };
    }

    public async Task<StorageSettingsDto> UpdateAsync(UpdateStorageSettingsRequest request, CancellationToken ct = default)
    {
        var repository = Normalize(request.RepositoryPath, "备份存放目录");
        var staging = Normalize(request.StagingPath, "上传暂存目录");

        // 上下限与 SequentialExecutionWorker / UploadProgressService 的 Clamp 一致。
        // 那两处夹到范围内就算了，这里必须报错：人在界面上填了 0，静默变成 1
        // 和静默变成"不限"是两个相反的结果，而他不会知道自己填的没生效。
        if (request.MaxConcurrentUploadsTotal is { } limit && (limit < 1 || limit > 64))
            throw new ValidationFailedException("同时上传的备份数上限要在 1 到 64 之间");

        // 两个根指向同一个目录会让暂存的半成品和正式备份混在一起：仓库目录名由主机名/任务名
        // 生成，暂存用的是 sessions\<guid>，撞不上，但保留策略扫的是整个仓库根——
        // 与其解释这里为什么"其实还能用"，不如直接不允许。
        if (repository is not null && staging is not null &&
            string.Equals(Path.GetFullPath(repository), Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase))
            throw new ValidationFailedException("备份存放目录和上传暂存目录不能是同一个目录");

        var beforeRepository = await _storage.ResolveRootAsync(UploadStorage.RepositorySettingKey, ct);
        var beforeStaging = await _storage.ResolveRootAsync(UploadStorage.StagingSettingKey, ct);
        var beforeLimit = await ReadUploadLimitAsync(ct);

        // 探测放在写库之前：任何一项不可用就整体拒绝，不留下"仓库改了、暂存没改"的半截状态。
        if (repository is not null)
            ProbeWritable(repository, "备份存放目录");
        if (staging is not null)
            ProbeWritable(staging, "上传暂存目录");

        await WriteSettingAsync(UploadStorage.RepositorySettingKey, repository, ct);
        await WriteSettingAsync(UploadStorage.StagingSettingKey, staging, ct);
        if (request.MaxConcurrentUploadsTotal is { } newLimit)
            await WriteRawSettingAsync(
                SequentialExecutionWorker.GlobalUploadLimitKey,
                newLimit.ToString(CultureInfo.InvariantCulture), ct);
        await _db.SaveChangesAsync(ct);

        // 60 秒缓存对刚点保存的人来说就是"改了没生效"，这里立刻作废
        _settings.Invalidate();

        await _audit.RecordAsync(
            "storage.update_settings", AuditResult.Success, "system_setting", null,
            beforeData: JsonSerializer.Serialize(new
            {
                repositoryPath = beforeRepository.Path,
                stagingPath = beforeStaging.Path,
                maxConcurrentUploadsTotal = beforeLimit
            }),
            afterData: JsonSerializer.Serialize(new
            {
                repositoryPath = repository,
                stagingPath = staging,
                maxConcurrentUploadsTotal = request.MaxConcurrentUploadsTotal ?? beforeLimit
            }),
            ct: ct);

        _logger.LogInformation(
            "存储设置已更新 repository={Repository} staging={Staging} uploadLimit={Limit}",
            repository ?? "(清除)", staging ?? "(清除)",
            request.MaxConcurrentUploadsTotal?.ToString(CultureInfo.InvariantCulture) ?? "(未改)");

        return await GetAsync(ct);
    }

    /// <summary>
    /// 浏览服务端本机目录。
    ///
    /// 只枚举目录名，不碰文件内容，并且只对 system.manage 开放——能改存储路径的人本来就能
    /// 把备份写到本机任意目录，让他先看一眼目录树不额外扩大任何权限，却省掉了"把路径背下来
    /// 再手敲一遍"这件事（敲错一个字符的结果是备份默默写进另一个目录）。
    /// </summary>
    public Task<StorageBrowseResponseDto> BrowseAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ListDrives());

        var target = path.Trim();
        if (!Path.IsPathFullyQualified(target))
            throw new ValidationFailedException("浏览路径必须是绝对路径");

        var full = Path.GetFullPath(target);
        if (!Directory.Exists(full))
            throw new BusinessException("NOT_FOUND", $"目录不存在：{full}", 404);

        var response = new StorageBrowseResponseDto
        {
            Path = full,
            ParentPath = Directory.GetParent(full)?.FullName
        };

        var denied = 0;
        var names = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(full))
            {
                ct.ThrowIfCancellationRequested();

                // 逐项判权限：整个目录能列，不代表每个子目录都能进。
                // 单项失败只计数，不能让整次浏览失败——否则一个装了权限的子目录
                // 就能让上级目录在界面上彻底打不开。
                try
                {
                    var info = new DirectoryInfo(directory);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    names.Add(info.Name);
                }
                catch (UnauthorizedAccessException) { denied++; }
                catch (IOException) { denied++; }

                if (names.Count >= MaxBrowseEntries)
                {
                    response.Truncated = true;
                    break;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new BusinessException("STORAGE_PATH_UNUSABLE", $"没有权限读取目录 {full}", 403);
        }

        response.DeniedCount = denied;
        response.Entries = names
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new StorageBrowseEntryDto { Name = n, Path = Path.Combine(full, n) })
            .ToList();

        return Task.FromResult(response);
    }

    /// <summary>磁盘列表。跳过未就绪的驱动器——光驱和没插的可移动盘选了也没意义</summary>
    private static StorageBrowseResponseDto ListDrives()
    {
        var response = new StorageBrowseResponseDto { IsDriveList = true };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.Ram)
                    continue;

                response.Entries.Add(new StorageBrowseEntryDto
                {
                    Name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.Name
                        : $"{drive.Name}（{drive.VolumeLabel}）",
                    Path = drive.RootDirectory.FullName,
                    IsDrive = true,
                    FreeBytes = drive.AvailableFreeSpace,
                    TotalBytes = drive.TotalSize
                });
            }
            catch (IOException) { /* 驱动器在枚举途中掉线，跳过 */ }
            catch (UnauthorizedAccessException) { /* 同上 */ }
        }

        return response;
    }

    /// <summary>解析一个存储根并实地探测其状态（只读探测，不创建目录）</summary>
    private async Task<StoragePathDto> DescribeAsync(string settingKey, CancellationToken ct)
    {
        var resolved = await _storage.ResolveRootAsync(settingKey, ct);
        var dto = new StoragePathDto
        {
            ConfiguredPath = await _settings.GetStringAsync(settingKey, ct),
            EffectivePath = resolved.Path,
            Source = resolved.Source
        };

        try
        {
            var full = Path.GetFullPath(resolved.Path);
            dto.Exists = Directory.Exists(full);
            dto.Writable = dto.Exists && CanWrite(full);
            if (!dto.Exists)
                dto.Problem = "目录还不存在，下一次上传时会自动创建";
            else if (!dto.Writable)
                dto.Problem = "目录存在但服务账户无法写入，请检查 NTFS 权限";

            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.IsReady)
            {
                dto.FreeBytes = drive.AvailableFreeSpace;
                dto.TotalBytes = drive.TotalSize;
            }
            else
            {
                dto.Problem = $"驱动器 {drive.Name} 未就绪";
            }
        }
        catch (Exception ex)
        {
            // 路径非法、盘符不存在、网络路径不可达都走这里。设置页本身必须还能打开——
            // 打不开的话，管理员连"当前配的是哪个错路径"都看不到，也就无从改正。
            dto.Problem = $"无法探测该路径：{ex.Message}";
            _logger.LogWarning(ex, "存储路径探测失败 key={Key} path={Path}", settingKey, resolved.Path);
        }

        return dto;
    }

    /// <summary>
    /// 统计不在当前仓库根之内的备份集。
    /// 仓库根改过之后这些备份集的文件仍然可读可恢复，但保留策略的物理删除会被围栏拦下
    /// （见 RetentionCleanupWorker.DeleteRepositoryDirectory），所以要在界面上说清楚。
    /// </summary>
    private async Task<int> CountBackupSetsOutsideRootAsync(string repositoryRoot, CancellationToken ct)
    {
        try
        {
            var prefix = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
            return await _db.BackupSets
                .AsNoTracking()
                .Where(b => b.Status != BackupSetStatus.Deleted
                            && b.RepositoryPath != null
                            && !b.RepositoryPath.ToLower().StartsWith(prefix))
                .CountAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "统计仓库根之外的备份集失败，按 0 显示");
            return 0;
        }
    }

    /// <summary>规范化输入：留空 → null（表示清除配置），否则校验为可用的绝对路径</summary>
    private static string? Normalize(string? input, string label)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var value = input.Trim();

        if (value.Length > MaxPathLength)
            throw new ValidationFailedException($"{label}过长（上限 {MaxPathLength} 字符）");

        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ValidationFailedException($"{label}含有非法字符");

        if (!Path.IsPathFullyQualified(value))
            throw new ValidationFailedException($"{label}必须是绝对路径，例如 D:\\BackupRepository 或 \\\\nas\\backup");

        var full = Path.GetFullPath(value);

        // 盘符根（D:\）会让备份直接铺在整个磁盘的根目录上，而保留策略删除时的
        // "至少两级目录"守卫会拒绝清理这类路径下的备份集——建出来就是个删不掉的仓库。
        // UNC 共享根（\\nas\backup）同理：GetRelativePath 得到 "."，一样过不了那道守卫。
        var root = Path.GetPathRoot(full);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            // 示例路径用 Path.Combine 拼：盘符根自带尾部反斜杠，UNC 共享根不带，
            // 直接字符串相加会给出 \\nas\backupBackupRepository 这种照抄就错的示例。
            var example = Path.Combine(full, "BackupRepository");
            throw new ValidationFailedException($"{label}不能是驱动器或共享的根目录，请指定一个子目录，例如 {example}");
        }

        return full;
    }

    /// <summary>建目录 + 写探针文件，验证服务账户确实能写</summary>
    private static void ProbeWritable(string path, string label)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            throw new BusinessException("STORAGE_PATH_UNUSABLE", $"{label} {path} 无法创建：{ex.Message}", 400);
        }

        var probe = Path.Combine(path, $".bm-write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, [0x62, 0x6d]);
        }
        catch (Exception ex)
        {
            throw new BusinessException("STORAGE_PATH_UNUSABLE",
                $"{label} {path} 不可写：{ex.Message}。请确认服务账户对该目录有写权限", 400);
        }
        finally
        {
            try { File.Delete(probe); } catch { /* 探针清理失败不影响判定 */ }
        }
    }

    /// <summary>只读可写性探测（GET 用，失败即视为不可写，不抛出）</summary>
    private static bool CanWrite(string path)
    {
        var probe = Path.Combine(path, $".bm-write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, [0x62, 0x6d]);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(probe); } catch { /* 同上 */ }
        }
    }

    /// <summary>写入一个存储根设置；value 为 null 时写 jsonb null（= 未配置，走回退链）</summary>
    private async Task WriteSettingAsync(string key, string? value, CancellationToken ct)
    {
        var json = value is null ? "null" : JsonSerializer.Serialize(value);
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == key, ct);

        if (row is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                SettingKey = key,
                SettingValue = json,
                Encrypted = false,
                UpdatedBy = _context.UserId
            });
            return;
        }

        row.SettingValue = json;
        row.UpdatedBy = _context.UserId;
    }

    /// <summary>
    /// 写一个已经是 JSON 字面量的设置值（数字、布尔）。
    /// 与 WriteSettingAsync 分开是因为那一个会把字符串再序列化一次——
    /// 4 写进去会变成 "4"，而 GetIntAsync 只认 JsonValueKind.Number，读出来永远是默认值。
    /// </summary>
    private async Task WriteRawSettingAsync(string key, string json, CancellationToken ct)
    {
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == key, ct);

        if (row is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                SettingKey = key,
                SettingValue = json,
                Encrypted = false,
                UpdatedBy = _context.UserId
            });
            return;
        }

        row.SettingValue = json;
        row.UpdatedBy = _context.UserId;
    }

    /// <summary>当前生效的全局上传上限。默认值与两个消费方保持一致。</summary>
    private Task<int> ReadUploadLimitAsync(CancellationToken ct) =>
        _settings.GetIntAsync(SequentialExecutionWorker.GlobalUploadLimitKey, 4, ct);
}
