using System.Security.Cryptography;
using System.Text;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Recognition;

namespace BackupMonitor.Agent;

public sealed class BackupScanResult
{
    public Guid TaskId { get; init; }
    public string CandidateKey { get; init; } = string.Empty;
    public string SourceRoot { get; init; } = string.Empty;
    public PrecheckBusinessUnitDto? BusinessUnit { get; init; }
    public string Status { get; init; } = "failed";
    public DateTime? BackupBusinessTime { get; init; }
    public string? QuickFingerprint { get; init; }
    public string? ManifestHash { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public List<PrecheckFileDto> Files { get; init; } = [];
}

/// <summary>
/// 按识别规则在真实文件系统上找出"一份备份"。
///
/// 规则的解析与匹配语义不在这里，而在 BackupMonitor.Shared.Recognition.RecognizerRules：
/// 服务端的配置向导要在目录快照上预演同一套规则，两份实现迟早分叉，
/// 而分叉的表现是向导说的和实际扫的不一致且无人察觉。这里只负责"怎么遍历磁盘"。
/// </summary>
public sealed class BackupScanner
{
    private readonly ILogger<BackupScanner> _logger;

    public BackupScanner(ILogger<BackupScanner> logger)
    {
        _logger = logger;
    }

    public async Task<List<BackupScanResult>> ScanAsync(AgentTaskConfigDto task, CancellationToken ct)
    {
        var source = Environment.ExpandEnvironmentVariables(task.SourcePath);
        var rules = RecognizerRules.Parse(task.RecognizerConfig);

        // 通配展开必须排在 Directory.Exists 之前——带 * 的路径本身当然不存在。
        // 只在真的写了 * 时才走这条路径，不带通配的源路径行为与从前逐字节相同。
        if (WildcardPath.ContainsWildcard(source))
        {
            var expansion = WildcardPath.Expand(
                source,
                current => SafeEnumerateChildDirectories(current, rules),
                directory => directory.Name,
                directory => directory.FullName,
                directory => NewestAllowedFileTime(directory.FullName, rules));
            if (!expansion.Success)
                return [Failure(task, source, "path_not_found", expansion.FailureMessage!)];

            source = expansion.Path!;
        }

        if (!Directory.Exists(source))
        {
            return [Failure(task, source, "path_not_found", $"源目录不存在：{source}")];
        }

        var roots = SelectRoots(source, task.RecognizerType, rules);
        var results = new List<BackupScanResult>();

        foreach (var selected in roots)
        {
            ct.ThrowIfCancellationRequested();
            var files = CollectFiles(selected.Root, rules, ct);

            // groupBy 把「同目录下的一组文件」收敛成一份备份。不套这一层的话，
            // 致远 OA 的 08 目录里 30 天的备份会被当成一份：manifest 每天变一次、
            // candidateKey 跟着变，永远不代表某一天的那份；而且每次扫描都要把 30 个
            // zip 全部读一遍算 SHA-256，按每个 2GB 算是一次 60GB 的磁盘 I/O。
            //
            // 位置很关键：必须排在 files.Count == 0 判断之前——分组后为空同样应当走
            // no_new_backup，而不是拿一个空集合往下算 manifest。
            // 分组语义本身在 RecognizerRules 里，与服务端预演共用同一份实现。
            files = rules.SelectLatestGroup(
                files,
                f => Path.GetRelativePath(selected.Root, f.FullName),
                f => f.LastWriteTimeUtc).ToList();

            var businessUnit = selected.BusinessUnit;
            if (files.Count == 0)
            {
                results.Add(Failure(task, selected.Root, "no_new_backup", "未发现符合识别规则的备份文件", businessUnit));
                continue;
            }

            var newest = files.Max(f => f.LastWriteTimeUtc);
            var stabilitySeconds = Math.Max(0, task.StabilityIntervalSeconds);
            if (stabilitySeconds > 0 && DateTime.UtcNow - newest < TimeSpan.FromSeconds(stabilitySeconds))
            {
                results.Add(Failure(task, selected.Root, "still_changing", "最新文件仍处于稳定观察窗口", businessUnit));
                continue;
            }

            var fileDtos = new List<PrecheckFileDto>(files.Count);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(selected.Root, file.FullName).Replace('\\', '/');
                fileDtos.Add(new PrecheckFileDto
                {
                    RelativePath = relative,
                    SizeBytes = file.Length,
                    LastModifiedAt = file.LastWriteTimeUtc,
                    Sha256 = await ComputeSha256Async(file.FullName, ct),
                    QuickHash = await ComputeQuickHashAsync(file.FullName, ct),
                    // 这里曾经只用 GlobMatch(relative, pattern)，而缺失判定用的是 MatchesPath。
                    // 于是 requiredFiles 写 UFDATA.BAK、文件在子目录里时，判定算它到场、
                    // 界面却不给它标 [必需]——同一个事实在两处显示不一致。
                    IsRequired = rules.IsRequiredFile(relative)
                });
            }

            var manifest = string.Join('\n', fileDtos
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(f => $"{f.RelativePath}|{f.SizeBytes}|{f.LastModifiedAt.Ticks}|{f.Sha256}"));
            var manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
            var quickFingerprint = string.Join('\n', fileDtos
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(f => $"{f.RelativePath}|{f.SizeBytes}|{f.LastModifiedAt.Ticks}|{f.QuickHash}"));
            var candidateKey = $"{task.TaskId:N}:{businessUnit?.ExternalKey ?? "root"}:{manifestHash}";
            var missingRequired = rules.HasMissingRequired(fileDtos.Select(f => f.RelativePath));

            results.Add(new BackupScanResult
            {
                TaskId = task.TaskId,
                CandidateKey = candidateKey,
                SourceRoot = selected.Root,
                BusinessUnit = businessUnit,
                Status = missingRequired ? "required_file_missing" : "passed",
                BackupBusinessTime = newest,
                QuickFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(quickFingerprint))).ToLowerInvariant(),
                ManifestHash = manifestHash,
                FailureCode = missingRequired ? "REQUIRED_FILE_MISSING" : null,
                FailureMessage = missingRequired ? "识别规则要求的文件未全部出现" : null,
                Files = fileDtos
            });
        }

        return results;
    }

    private static BackupScanResult Failure(
        AgentTaskConfigDto task, string sourceRoot, string status, string message, PrecheckBusinessUnitDto? unit = null) =>
        new()
        {
            TaskId = task.TaskId,
            CandidateKey = $"{task.TaskId:N}:{unit?.ExternalKey ?? "root"}:{status}:{sourceRoot}",
            SourceRoot = sourceRoot,
            BusinessUnit = unit,
            Status = status,
            FailureCode = status.ToUpperInvariant(),
            FailureMessage = message
        };

    private List<(string Root, PrecheckBusinessUnitDto? BusinessUnit)> SelectRoots(string source, string recognizerType, RecognizerRules rules)
    {
        var normalized = recognizerType.Trim().ToLowerInvariant();
        if (normalized == "subdirectory_units")
        {
            try
            {
                var depth = rules.BusinessUnitDepth;
                var units = Directory.EnumerateDirectories(
                        source,
                        "*",
                        depth <= 1 ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
                    .Where(p => DirectoryDepth(source, p) == depth)
                    .Where(p => !rules.IsExcludedDirectory(p))
                    .Where(p => rules.BatchRegex is null || RecognizerRules.RegexMatches(Path.GetFileName(p), rules.BatchRegex))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(p =>
                    {
                        var relative = Path.GetRelativePath(source, p).Replace('\\', '/');
                        return (Path: p, Unit: (PrecheckBusinessUnitDto?)new PrecheckBusinessUnitDto
                        {
                            // ExternalKey 曾经取叶子目录名，depth≥2 时必然撞车：
                            // 账套001/20260824 与 账套002/20260824 的叶子名都是 20260824，
                            // 而业务单元的唯一键是 (task_id, external_key)——两个账套会被合并成同一个单元。
                            // 相对路径才是这个单元在源目录下的唯一身份。
                            ExternalKey = relative,
                            DisplayName = relative,
                            SourceRelativePath = relative
                        });
                    })
                    .ToList();

                // U8 这类结构是两层的：账套是业务单元，账套下每天一个备份目录。
                // 只按 depth 定位到账套的话，CollectFiles 会把所有天的文件混成一份，
                // 每天换一次 manifest，越滚越大且永远不代表"某一天的那份备份"。
                // unitLayout=latest_directory 让每个单元内部再取最新的那个子目录。
                if (rules.UnitLayout == "latest_directory")
                {
                    return units
                        .Select(entry => (Root: SelectLatestChildDirectory(entry.Path, rules), entry.Unit))
                        .ToList();
                }

                return units.Select(entry => (entry.Path, entry.Unit)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "枚举业务子目录失败 source={Source}", source);
                return [];
            }
        }

        if (normalized == "latest_directory")
        {
            var directories = SafeEnumerateDirectories(source, rules)
                .Where(d => rules.BatchRegex is null
                            || RecognizerRules.RegexMatches(Path.GetRelativePath(source, d.FullName).Replace('\\', '/'), rules.BatchRegex)
                            || RecognizerRules.RegexMatches(d.Name, rules.BatchRegex))
                .ToList();
            var latest = directories
                .Select(d => new
                {
                    Path = d.FullName,
                    Latest = SafeEnumerateFiles(d.FullName, SearchOption.AllDirectories, rules)
                        .Where(f => IsAllowed(f, d.FullName, rules))
                        .Select(f => f.LastWriteTimeUtc)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max()
                })
                .OrderByDescending(x => x.Latest)
                .FirstOrDefault();
            return latest is null ? [(source, null)] : [(latest.Path, null)];
        }

        // 整个源目录算一份备份。这是 multi_file_set 的**定义**，不是没匹配上的兜底——
        // 它一直靠落到函数末尾的默认返回工作，行为恰好正确，但那是巧合。
        // 写成显式分支，改动默认返回时才不会顺手把这个类型一起改坏。
        if (normalized == "multi_file_set")
            return [(source, null)];

        if (normalized == "latest_single_file")
        {
            var searchOption = rules.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var latest = SafeEnumerateFiles(source, searchOption, rules)
                .Where(f => IsAllowed(f, source, rules))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest is null ? [(source, null)] : [(latest.DirectoryName ?? source, null)];
        }

        return [(source, null)];
    }

    /// <summary>
    /// 取业务单元内最新的那个子目录。判定依据是"目录里最新文件的修改时间"，
    /// 而不是目录自身的时间戳——很多备份程序建好目录后才慢慢往里写，目录时间没有参考价值。
    /// 单元下还没有任何子目录时退回单元本身，让上层照常走"没有符合规则的文件"这条失败路径，
    /// 而不是凭空少掉一个业务单元。
    /// </summary>
    private static string SelectLatestChildDirectory(string unitRoot, RecognizerRules rules)
    {
        try
        {
            // 与通配展开用的是同一口径，抽成 NewestAllowedFileTime 共用：
            // 两处各写一遍的话，"最新"在两个地方会慢慢变成两个意思。
            var latest = Directory.EnumerateDirectories(unitRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(p => !rules.IsExcludedDirectory(p))
                .Select(p => new
                {
                    Path = p,
                    Latest = NewestAllowedFileTime(p, rules)
                })
                .OrderByDescending(x => x.Latest)
                .ThenByDescending(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return latest?.Path ?? unitRoot;
        }
        catch (Exception)
        {
            return unitRoot;
        }
    }

    private static List<FileInfo> CollectFiles(string root, RecognizerRules rules, CancellationToken ct)
    {
        var option = rules.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = SafeEnumerateFiles(root, option, rules)
            .Where(f => IsAllowed(f, root, rules))
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // latest_single_file 通过根目录选择后仍可能收集同目录其他文件；调用方只需要稳定且可上传的集合，
        // 因此这里保留规则过滤后的集合，避免误删同一备份集中的伴随文件。
        ct.ThrowIfCancellationRequested();
        return files;
    }

    /// <summary>把 FileInfo 折成共享规则需要的四个事实，判定本身在 RecognizerRules 里。</summary>
    private static bool IsAllowed(FileInfo file, string root, RecognizerRules rules)
    {
        var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
        bool hidden;
        try { hidden = (file.Attributes & FileAttributes.Hidden) != 0; }
        catch (Exception) { hidden = false; }

        return rules.IsAllowedFile(relative, file.FullName, file.Name, hidden, Path.GetFileName(root));
    }

    /// <summary>通配展开用：某个目录的直接子目录，受 excludeDirectories 约束。</summary>
    private static IEnumerable<DirectoryInfo> SafeEnumerateChildDirectories(string root, RecognizerRules rules)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(p => !rules.IsExcludedDirectory(p))
                .Select(p => new DirectoryInfo(p))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 目录内最新的、符合规则的文件时间。与 SelectLatestChildDirectory 同一口径：
    /// 不看目录自身的时间戳。空目录（例如被清空的 07）得到 DateTime.MinValue，排在最后。
    /// </summary>
    private static DateTime NewestAllowedFileTime(string directory, RecognizerRules rules) =>
        SafeEnumerateFiles(directory, SearchOption.AllDirectories, rules)
            .Where(f => IsAllowed(f, directory, rules))
            .Select(f => f.LastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(string root, RecognizerRules rules)
    {
        try
        {
            var option = rules.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateDirectories(root, "*", option)
                .Where(p => !rules.IsExcludedDirectory(p))
                .Select(p => new DirectoryInfo(p))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(string root, SearchOption option, RecognizerRules rules)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", option).Select(p =>
            {
                try { return new FileInfo(p); } catch { return null; }
            })
            .Where(f => f is not null)
            .Select(f => f!)
            .Where(f => !rules.IsExcludedDirectory(f.DirectoryName))
            .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static int DirectoryDepth(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative is "." or "")
            return 0;
        return relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ComputeQuickHashAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        var sampleSize = (int)Math.Min(64 * 1024, info.Length);
        var bytes = new byte[sampleSize];
        var read = await stream.ReadAsync(bytes.AsMemory(), ct);
        if (info.Length > sampleSize)
        {
            stream.Seek(Math.Max(0, info.Length - sampleSize), SeekOrigin.Begin);
            var tail = new byte[sampleSize];
            var tailRead = await stream.ReadAsync(tail.AsMemory(), ct);
            bytes = bytes[..read].Concat(tail[..tailRead]).ToArray();
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
