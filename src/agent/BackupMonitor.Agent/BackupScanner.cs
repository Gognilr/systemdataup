using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackupMonitor.Shared.Models.Agent;

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

public sealed class BackupScanner
{
    private sealed record Rules(
        List<string> Includes,
        List<string> Excludes,
        List<string> Required,
        List<string> ExcludeDirectories,
        bool Recursive,
        string? BatchRegex,
        int BusinessUnitDepth = 1);

    private readonly ILogger<BackupScanner> _logger;

    public BackupScanner(ILogger<BackupScanner> logger)
    {
        _logger = logger;
    }

    public async Task<List<BackupScanResult>> ScanAsync(AgentTaskConfigDto task, CancellationToken ct)
    {
        var source = Environment.ExpandEnvironmentVariables(task.SourcePath);
        if (!Directory.Exists(source))
        {
            return [Failure(task, source, "path_not_found", $"源目录不存在：{source}")];
        }

        var rules = ParseRules(task.RecognizerConfig);
        var roots = SelectRoots(source, task.RecognizerType, rules);
        var results = new List<BackupScanResult>();

        foreach (var selected in roots)
        {
            ct.ThrowIfCancellationRequested();
            var files = CollectFiles(selected.Root, rules, ct);
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
                    IsRequired = rules.Required.Count == 0 || rules.Required.Any(pattern => GlobMatch(relative, pattern))
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
            var missingRequired = rules.Required.Any(pattern =>
                !fileDtos.Any(file => MatchesPath(file.RelativePath, pattern)));

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

    private List<(string Root, PrecheckBusinessUnitDto? BusinessUnit)> SelectRoots(string source, string recognizerType, Rules rules)
    {
        var normalized = recognizerType.Trim().ToLowerInvariant();
        if (normalized == "subdirectory_units")
        {
            try
            {
                var depth = rules.BusinessUnitDepth;
                return Directory.EnumerateDirectories(
                        source,
                        "*",
                        depth <= 1 ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
                    .Where(p => DirectoryDepth(source, p) == depth)
                    .Where(p => !IsExcludedDirectory(p, rules.ExcludeDirectories))
                    .Where(p => rules.BatchRegex is null || RegexMatches(Path.GetFileName(p), rules.BatchRegex))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(p => (p, (PrecheckBusinessUnitDto?)new PrecheckBusinessUnitDto
                    {
                        ExternalKey = Path.GetFileName(p),
                        DisplayName = Path.GetFileName(p),
                        SourceRelativePath = Path.GetRelativePath(source, p).Replace('\\', '/')
                    }))
                    .ToList();
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
                            || RegexMatches(Path.GetRelativePath(source, d.FullName).Replace('\\', '/'), rules.BatchRegex)
                            || RegexMatches(d.Name, rules.BatchRegex))
                .ToList();
            var latest = directories
                .Select(d => new
                {
                    Path = d.FullName,
                    Latest = SafeEnumerateFiles(d.FullName, SearchOption.AllDirectories, rules.ExcludeDirectories)
                        .Where(f => IsAllowed(f, d.FullName, rules))
                        .Select(f => f.LastWriteTimeUtc)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max()
                })
                .OrderByDescending(x => x.Latest)
                .FirstOrDefault();
            return latest is null ? [(source, null)] : [(latest.Path, null)];
        }

        if (normalized == "latest_single_file")
        {
            var searchOption = rules.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var latest = SafeEnumerateFiles(source, searchOption, rules.ExcludeDirectories)
                .Where(f => IsAllowed(f, source, rules))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest is null ? [(source, null)] : [(latest.DirectoryName ?? source, null)];
        }

        return [(source, null)];
    }

    private static List<FileInfo> CollectFiles(string root, Rules rules, CancellationToken ct)
    {
        var option = rules.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = SafeEnumerateFiles(root, option, rules.ExcludeDirectories)
            .Where(f => IsAllowed(f, root, rules))
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // latest_single_file 通过根目录选择后仍可能收集同目录其他文件；调用方只需要稳定且可上传的集合，
        // 因此这里保留规则过滤后的集合，避免误删同一备份集中的伴随文件。
        ct.ThrowIfCancellationRequested();
        return files;
    }

    private static Rules ParseRules(string? json)
    {
        var result = new Rules([], [], [], [], true, null, 1);
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            result.Includes.AddRange(ReadStrings(root, "includePatterns"));
            result.Includes.AddRange(ReadStrings(root, "includes"));
            result.Excludes.AddRange(ReadStrings(root, "excludePatterns"));
            result.Excludes.AddRange(ReadStrings(root, "excludes"));
            result.ExcludeDirectories.AddRange(ReadStrings(root, "excludeDirectories"));
            result.ExcludeDirectories.AddRange(ReadStrings(root, "excludeDirs"));
            result.Required.AddRange(ReadStrings(root, "requiredFiles"));
            result.Required.AddRange(ReadStrings(root, "requiredPatterns"));
            result.Required.AddRange(ReadStrings(root, "required"));
            if (root.TryGetProperty("recursive", out var recursive)
                && (recursive.ValueKind == JsonValueKind.True || recursive.ValueKind == JsonValueKind.False))
            {
                result = result with { Recursive = recursive.GetBoolean() };
            }

            if (root.TryGetProperty("businessUnitDepth", out var depth)
                && depth.TryGetInt32(out var parsedDepth)
                && parsedDepth > 0)
            {
                result = result with { BusinessUnitDepth = Math.Min(parsedDepth, 32) };
            }

            if (root.TryGetProperty("batchRegex", out var batchRegex)
                && batchRegex.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(batchRegex.GetString()))
            {
                result = result with { BatchRegex = batchRegex.GetString() };
            }
        }
        catch
        {
            // 无效的识别规则仍允许基础文件发现，服务端会在配置校验阶段提示管理员。
        }

        return result;
    }

    private static IEnumerable<string> ReadStrings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!];
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s))
            : [];
    }

    private static bool IsAllowed(FileInfo file, string root, Rules rules)
    {
        if ((file.Attributes & FileAttributes.Hidden) != 0)
            return false;
        if (file.Name.StartsWith('~') || file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || file.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || file.Name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
        if (rules.Excludes.Any(pattern => MatchesPath(relative, pattern) || GlobMatch(file.FullName, pattern)))
            return false;
        if (rules.BatchRegex is not null
            && !RegexMatches(relative, rules.BatchRegex)
            && !RegexMatches(Path.GetFileName(root), rules.BatchRegex))
            return false;
        return rules.Includes.Count == 0 || rules.Includes.Any(pattern => MatchesPath(relative, pattern) || GlobMatch(file.FullName, pattern));
    }

    private static bool MatchesPath(string relative, string pattern) =>
        GlobMatch(relative, pattern) || GlobMatch(Path.GetFileName(relative), pattern);

    private static bool GlobMatch(string value, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value.Replace('\\', '/'), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(string root, Rules rules)
    {
        try
        {
            var option = rules.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateDirectories(root, "*", option)
                .Where(p => !IsExcludedDirectory(p, rules.ExcludeDirectories))
                .Select(p => new DirectoryInfo(p))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(
        string root,
        SearchOption option,
        IReadOnlyCollection<string> excludedDirectories)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", option).Select(p =>
            {
                try { return new FileInfo(p); } catch { return null; }
            })
            .Where(f => f is not null)
            .Select(f => f!)
            .Where(f => !IsExcludedDirectory(f.DirectoryName, excludedDirectories))
            .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsExcludedDirectory(string? path, IReadOnlyCollection<string> excludedDirectories)
    {
        if (string.IsNullOrWhiteSpace(path) || excludedDirectories.Count == 0)
            return false;

        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => excludedDirectories.Any(excluded =>
            GlobMatch(segment, excluded.Trim())));
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

    private static bool RegexMatches(string value, string pattern)
    {
        try
        {
            return Regex.IsMatch(
                value,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
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
