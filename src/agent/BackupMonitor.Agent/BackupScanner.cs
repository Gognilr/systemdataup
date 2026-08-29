using System.Runtime.CompilerServices;
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

    /// <summary>
    /// 试扫（识别测试）产物：没算 SHA-256，因此 ManifestHash / QuickFingerprint / CandidateKey 都是空的。
    /// 这种结果只能拿去给人看，绝不能当预检结果上报——SubmitOneScanAsync 会挡住它。
    /// </summary>
    public bool IsTestScan { get; init; }
}

/// <summary>
/// 按识别规则在真实文件系统上找出"一份备份"。
///
/// 规则的解析与匹配语义不在这里，而在 BackupMonitor.Shared.Recognition.RecognizerRules：
/// 服务端的配置向导要在目录快照上预演同一套规则，两份实现迟早分叉，
/// 而分叉的表现是向导说的和实际扫的不一致且无人察觉。这里只负责"怎么遍历磁盘"。
/// </summary>
/// <summary>
/// 扫描进度的一条快照。
///
/// 存在的理由：扫一个 18 账套的 U8 备份盘要把几十 GB 读一遍算 SHA-256，
/// 这期间管理端只看得到指令状态 running，日志也一声不吭——
/// 「正在算」和「卡死了」在界面上是同一个样子，而人只能干等。
/// </summary>
/// <param name="UnitIndex">当前是第几个业务单元（从 0 起）。</param>
/// <param name="UnitCount">这次扫描一共几个业务单元。</param>
/// <param name="Unit">业务单元的名字；整个源目录算一份时为空。</param>
/// <param name="Stage">collecting（找文件）/ hashing（算校验和）。</param>
/// <param name="File">当前正在读的文件名，没有具体文件时为空。</param>
/// <param name="FileIndex">单元内已处理到第几个文件（从 0 起）。</param>
/// <param name="FileCount">单元内一共几个文件。</param>
/// <summary>扫描进度回调。</summary>
public delegate Task ScanProgressCallback(ScanProgress progress, CancellationToken ct);

public readonly record struct ScanProgress(
    int UnitIndex, int UnitCount, string? Unit, string Stage, string? File, int FileIndex, int FileCount);

public sealed class BackupScanner
{
    private readonly ILogger<BackupScanner> _logger;

    public BackupScanner(ILogger<BackupScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 正式扫描：结果可以上报为预检结果。
    ///
    /// 两段式（D2）：先只算头尾采样的 QuickHash 拼出 QuickFingerprint，与服务端下发的
    /// 上一次基线（<see cref="AgentTaskConfigDto.LastQuickFingerprints"/>）比对；
    /// 一致就直接回 no_new_backup，整份备份一个字节都不用重读。不一致才进第二段算全量 SHA-256。
    /// </summary>
    /// <param name="forceFullHash">
    /// 强制完整校验：跳过基线比对，无条件走全量 SHA-256。
    /// QuickHash 只采样头尾各 64KB，「文件中段被改而 size 与 mtime 都没变」它认不出来——
    /// 这是刻意接受的边界（见 ScanCoreAsync 里的说明），而这个开关就是它的逃生门。
    /// </param>
    /// <param name="progress">
    /// 每处理完一步调一次，用来把「扫到第几个账套、正在读哪个文件」送回管理端。
    /// 传 null 表示不需要——本身失败绝不能影响扫描，节流与吞异常由调用方负责。
    /// </param>
    public IAsyncEnumerable<BackupScanResult> ScanAsync(
        AgentTaskConfigDto task, bool forceFullHash, ScanProgressCallback? progress, CancellationToken ct)
        => ScanCoreAsync(task, testOnly: false, forceFullHash, progress, ct);

    /// <summary>正式扫描（不强制完整校验、不上报进度）。</summary>
    public IAsyncEnumerable<BackupScanResult> ScanAsync(AgentTaskConfigDto task, CancellationToken ct)
        => ScanCoreAsync(task, testOnly: false, forceFullHash: false, progress: null, ct);

    /// <summary>
    /// 试扫（识别测试）：只回答"会挑中哪些文件、判定通不通过"，不算 SHA-256。
    ///
    /// 这是识别测试从"几十秒"回到"秒级"的关键。它和正式扫描共用同一套遍历与识别逻辑
    /// （否则试扫说的和实际扫的会分叉），区别只在于跳过全量哈希：
    /// 界面用到的只有 relativePath / sizeBytes / lastModifiedAt / isRequired，
    /// 而人在对话框前面等着——为了几个不看的哈希值把 2GB 的 zip 整读一遍不值得。
    /// </summary>
    public async Task<List<BackupScanResult>> ScanForTestAsync(AgentTaskConfigDto task, CancellationToken ct)
    {
        var results = new List<BackupScanResult>();
        await foreach (var scan in ScanCoreAsync(task, testOnly: true, forceFullHash: false, progress: null, ct))
            results.Add(scan);
        return results;
    }

    /// <summary>
    /// 逐个业务单元产出结果，而不是全部扫完再一次性返回。
    ///
    /// 这一条直接决定「什么时候能开始传」：18 个账套的 U8 备份盘，全扫完才提交的话，
    /// 第 1 个账套哪怕两分钟就好了，也要等第 18 个读完几十 GB——在此之前一条上传都不会
    /// 下发，「传输中」页面空着，人只能猜是不是卡了。逐个产出之后，扫描与上传自然重叠。
    /// </summary>
    private async IAsyncEnumerable<BackupScanResult> ScanCoreAsync(
        AgentTaskConfigDto task, bool testOnly, bool forceFullHash,
        ScanProgressCallback? progress, [EnumeratorCancellation] CancellationToken ct)
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
            {
                yield return Failure(task, source, "path_not_found", expansion.FailureMessage!, testOnly);
                yield break;
            }

            source = expansion.Path!;
        }

        if (!Directory.Exists(source))
        {
            yield return Failure(task, source, "path_not_found", $"源目录不存在：{source}", testOnly);
            yield break;
        }

        var roots = SelectRoots(source, task.RecognizerType, rules);

        for (var unitIndex = 0; unitIndex < roots.Count; unitIndex++)
        {
            var selected = roots[unitIndex];
            ct.ThrowIfCancellationRequested();
            var unitName = selected.BusinessUnit?.DisplayName;
            await Report(progress, new ScanProgress(unitIndex, roots.Count, unitName, "collecting", null, 0, 0), ct);
            var files = CollectFiles(selected.Root, rules, ct);

            // groupBy 把「同目录下的一组文件」收敛成一份备份。不套这一层的话，
            // 致远 OA 的 08 目录里 30 天的备份会被当成一份：manifest 每天变一次、
            // candidateKey 跟着变，永远不代表某一天的那份；而且每次扫描都要把 30 个
            // zip 全部读一遍算 SHA-256，按每个 2GB 算是一次 60GB 的磁盘 I/O。
            //
            // 位置很关键：必须排在 files.Count == 0 判断之前——分组后为空同样应当走
            // no_new_backup，而不是拿一个空集合往下算 manifest。
            // 分组语义本身在 RecognizerRules 里，与服务端预演共用同一份实现。
            files = rules.NarrowToOneBackup(
                task.RecognizerType,
                files,
                f => Path.GetRelativePath(selected.Root, f.FullName),
                f => f.LastWriteTimeUtc).ToList();

            var businessUnit = selected.BusinessUnit;
            if (files.Count == 0)
            {
                yield return Failure(task, selected.Root, "no_new_backup", "未发现符合识别规则的备份文件", testOnly, businessUnit);
                continue;
            }

            var newest = files.Max(f => f.LastWriteTimeUtc);
            var stabilitySeconds = Math.Max(0, task.StabilityIntervalSeconds);
            if (stabilitySeconds > 0 && DateTime.UtcNow - newest < TimeSpan.FromSeconds(stabilitySeconds))
            {
                yield return Failure(task, selected.Root, "still_changing", "最新文件仍处于稳定观察窗口", testOnly, businessUnit);
                continue;
            }

            // ── 第一段：只算便宜的 ──
            // QuickHash 读头尾各 64KB，全量 SHA-256 要把文件整读一遍。判断「这份是不是
            // 上次那一份」所需的信息（路径 + 大小 + mtime + 头尾采样）在读第一个字节之前
            // 就已经够了；全量哈希是为 manifest 完整性算的，不该为了一次「有没有新备份」
            // 的问答把 1.5GB 重读一遍。
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
                    // 试扫连 QuickHash 都不算：界面一个哈希值都不看。
                    QuickHash = testOnly ? null : await ComputeQuickHashAsync(file.FullName, ct),
                    // 这里曾经只用 GlobMatch(relative, pattern)，而缺失判定用的是 MatchesPath。
                    // 于是 requiredFiles 写 UFDATA.BAK、文件在子目录里时，判定算它到场、
                    // 界面却不给它标 [必需]——同一个事实在两处显示不一致。
                    IsRequired = rules.IsRequiredFile(relative)
                });
            }

            var missingRequired = rules.HasMissingRequired(fileDtos.Select(f => f.RelativePath));

            // 分卷归档的卷号连续性（volumeContinuity）。与「必需文件在不在」分开问：
            // 缺第 2 卷时第 1、3、4 卷都在场，requiredFiles 会说「齐了」，而整份归档解不开。
            // 判定共用 RecognizerRules.FindVolumeGap，与服务端预演是同一段代码。
            var volumeGap = missingRequired ? null : rules.FindVolumeGap(fileDtos.Select(f => f.RelativePath));
            var unitKey = businessUnit?.ExternalKey ?? "root";

            // 试扫没有哈希，manifestHash / candidateKey 也就无从谈起。
            // 这里刻意留空而不是拿空哈希凑一个出来——凑出来的 key 看着像真的，
            // 一旦哪天被误当作候选去重的依据，就是两份不同的备份撞成同一个候选。
            string? manifestHash = null;
            string? quickFingerprintHash = null;
            var candidateKey = string.Empty;
            if (!testOnly)
            {
                quickFingerprintHash = FingerprintOf(fileDtos, f => f.QuickHash);

                // ── 基线比对：与上次一致就到此为止，一个字节都不用重读 ──
                // 已知且刻意接受的边界：QuickHash 只采样头尾各 64KB，**文件中段被改而
                // size 与 mtime 都没变**时它认不出来，这里会判 no_new_backup。理由：
                //   1. 它只决定「要不要重算全量哈希」，真正入库 manifest 的仍然是全量 SHA-256；
                //   2. size 与 mtime 一起参与比对，要绕过它得同时保持这两者不变并只改中段，
                //      正常的备份程序不会产生这种文件；
                //   3. 真要防篡改，forceFullHash（界面上的「强制完整校验」）是明路。
                // 反过来，**绝不能**把 QuickFingerprint 直接当成 CandidateKey——
                // 那会让两份不同的备份撞成同一个候选，见下面 candidateKey 用的是 manifestHash。
                var baseline = task.LastQuickFingerprints
                    ?.FirstOrDefault(f => string.Equals(f.ExternalKey, unitKey, StringComparison.OrdinalIgnoreCase));
                if (!forceFullHash
                    && !string.IsNullOrWhiteSpace(baseline?.QuickFingerprint)
                    && string.Equals(baseline!.QuickFingerprint, quickFingerprintHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "快速指纹与上次一致，跳过全量哈希 taskId={TaskId} unit={Unit} files={Files}",
                        task.TaskId, unitKey, fileDtos.Count);
                    yield return Failure(task, selected.Root, "no_new_backup",
                        "快速指纹与上一次候选一致，未发现新的备份", testOnly, businessUnit);
                    continue;
                }

                // ── 第二段：真有新东西，该读的还是要读 ──
                // 这一段是整次扫描里最慢的部分（一个账套就是好几个 GB），进度也只有在这里
                // 报才有意义——报的是「第几个单元、正在读哪个文件」，不是一个空转的百分比。
                for (var i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Report(progress, new ScanProgress(
                        unitIndex, roots.Count, unitName, "hashing", files[i].Name, i, files.Count), ct);
                    fileDtos[i].Sha256 = await ComputeSha256Async(files[i].FullName, ct);
                }

                manifestHash = FingerprintOf(fileDtos, f => f.Sha256);
                candidateKey = $"{task.TaskId:N}:{unitKey}:{manifestHash}";
            }

            yield return new BackupScanResult
            {
                TaskId = task.TaskId,
                CandidateKey = candidateKey,
                SourceRoot = selected.Root,
                BusinessUnit = businessUnit,
                Status = missingRequired || volumeGap is not null ? "required_file_missing" : "passed",
                BackupBusinessTime = newest,
                QuickFingerprint = quickFingerprintHash,
                ManifestHash = manifestHash,
                IsTestScan = testOnly,
                FailureCode = missingRequired || volumeGap is not null ? "REQUIRED_FILE_MISSING" : null,
                FailureMessage = missingRequired
                    ? "识别规则要求的文件未全部出现"
                    : volumeGap,
                Files = fileDtos
            };
        }
    }

    /// <summary>
    /// 上报一条进度。吞掉一切异常：进度是装饰，绝不能让它把正在跑的扫描搞失败——
    /// 与 ProgressReporter 里那条同样的约定，只是这里连节流都交给调用方。
    /// </summary>
    private async Task Report(ScanProgressCallback? progress, ScanProgress info, CancellationToken ct)
    {
        if (progress is null)
            return;
        try { await progress(info, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "扫描进度上报失败（不影响扫描本身）");
        }
    }

    private static BackupScanResult Failure(
        AgentTaskConfigDto task, string sourceRoot, string status, string message, bool testOnly, PrecheckBusinessUnitDto? unit = null) =>
        new()
        {
            TaskId = task.TaskId,
            CandidateKey = $"{task.TaskId:N}:{unit?.ExternalKey ?? "root"}:{status}:{sourceRoot}",
            SourceRoot = sourceRoot,
            BusinessUnit = unit,
            Status = status,
            FailureCode = status.ToUpperInvariant(),
            FailureMessage = message,
            IsTestScan = testOnly
        };

    private List<(string Root, PrecheckBusinessUnitDto? BusinessUnit)> SelectRoots(string source, string recognizerType, RecognizerRules rules)
    {
        var normalized = recognizerType.Trim().ToLowerInvariant();
        if (normalized == "subdirectory_units")
        {
            try
            {
                // 单元的定位规则（按人指定的路径 / 按日期层自适应 / 按固定深度）住在
                // BusinessUnitResolver 里，与服务端预演共用同一份实现——两侧各写一遍的话，
                // 向导说会识别出 18 个账套、实际扫描出 3 个，而且没有任何人会发现。
                //
                // 刻意不传 maxDepth：下探层数从 rules.UnitMaxDepth 取（向导推断时写进配置的那个数）。
                // 这里取默认值的话，真实文件系统比快照深时 date_leaf 会比向导多下探两层，
                // 扫出来的单元集合与向导展示的静默地不是同一批。
                var units = BusinessUnitResolver.Resolve(
                    new DirectoryInfo(source),
                    rules,
                    DirectoryNavigator(source));

                // 单元里再取「最新的那个日期目录」。
                // 只定位到账套而不再往下走的话，CollectFiles 会把所有天的文件混成一份，
                // 每天换一次 manifest，越滚越大且永远不代表「某一天的那份备份」。
                // date_leaf 按定义就带这一步（它找的正是「下面是日期目录」的那一层）。
                var pickLatestChild = rules.UnitLayout is "latest_directory" or "date_leaf";

                return units
                    .Select(unit =>
                    {
                        var relative = unit.RelativePath;
                        var dto = (PrecheckBusinessUnitDto?)new PrecheckBusinessUnitDto
                        {
                            // ExternalKey 曾经取叶子目录名，depth≥2 时必然撞车：
                            // 账套001/20260824 与 账套002/20260824 的叶子名都是 20260824，
                            // 而业务单元的唯一键是 (task_id, external_key)——两个账套会被合并成同一个单元。
                            // 相对路径才是这个单元在源目录下的唯一身份，混合层级下同样成立：
                            // ZT001 与 ZT201-ZT216/ZT201 天然不撞键。
                            ExternalKey = relative,
                            DisplayName = relative,
                            SourceRelativePath = relative
                        };
                        var root = pickLatestChild
                            ? SelectLatestChildDirectory(unit.Directory.FullName, rules)
                            : unit.Directory.FullName;
                        return (Root: root, Unit: dto);
                    })
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

        // 这里只负责「按规则收全」，收敛成一份备份是 NarrowToOneBackup 的事（单一收敛点）。
        // 原先这里有一句注释说 latest_single_file 会连带收同目录其他文件、且刻意保留——
        // 那正是让这个识别器名不副实的地方：在堆了 7 天备份的目录上，「取最新的那个文件」
        // 实际取的是全部 14 个。
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

    /// <summary>把真实文件系统折成 BusinessUnitResolver 需要的四个访问器。</summary>
    private static BusinessUnitResolver.Navigator<DirectoryInfo> DirectoryNavigator(string source) => new()
    {
        ChildDirectories = directory =>
        {
            try { return directory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception) { return []; }
        },
        Name = directory => directory.Name,
        FullPath = directory => directory.FullName,
        RelativePath = directory => Path.GetRelativePath(source, directory.FullName).Replace('\\', '/')
    };

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

    /// <summary>
    /// 把文件清单拼成一份指纹。manifestHash 与 quickFingerprint 走同一个函数，
    /// 差别只在于取哪一列哈希——两处各写一遍排序与分隔符，迟早只改其中一处。
    /// </summary>
    private static string FingerprintOf(List<PrecheckFileDto> files, Func<PrecheckFileDto, string?> hash)
    {
        var text = string.Join('\n', files
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{f.RelativePath}|{f.SizeBytes}|{f.LastModifiedAt.Ticks}|{hash(f)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
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
