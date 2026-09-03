using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

public sealed class AgentState
{
    public string MachineId { get; set; } = string.Empty;
    public Guid? RegistrationId { get; set; }
    public Guid? ClientId { get; set; }
    public long ConfigVersion { get; set; }
    public string? PublicKeySpki { get; set; }
    public string? ProtectedPrivateKey { get; set; }
    public string? ProtectedCertificatePfx { get; set; }
    public string? CertificateThumbprint { get; set; }
    public DateTime? CertificateExpiresAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// 本 Agent 认定的服务端实例 ID（LAN 发现应答里的 ServerInstanceId）。
    ///
    /// 安装向导发现到哪台服务端就写哪个值。运行期地址失联后重新发现时，
    /// 它是「这台应答的机器是不是我原来那台服务端」的两个固定锚点之一
    /// （另一个是 ServerCertificateFingerprint）——发现协议是无认证的明文 UDP，
    /// 少了这两个比对，同网段任何人广播一个应答就能把全部客户端劫走。
    /// 为空表示当时是手工填的地址、没见过实例 ID，此时自动重发现一律不做。
    /// </summary>
    public string? ServerInstanceId { get; set; }

    /// <summary>
    /// 运行期重新发现到的服务端地址，优先于 appsettings.json 里的 ServerUrl。
    ///
    /// 刻意不回写 appsettings.json：那个文件在 %ProgramFiles% 下、由安装器负责，
    /// Agent 去改它既要和安装器抢写，又会让「配置文件里写的」和「实际连的」
    /// 出现两个来源。地址属于运行期状态，和身份一起放在 state.json 里更自洽。
    /// </summary>
    public string? ServerUrlOverride { get; set; }
    public Dictionary<string, LocalCandidateState> Candidates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Guid> SeenNotificationIds { get; set; } = [];
    public List<string> SeenCommandNonces { get; set; } = [];

    /// <summary>按任务 ID 记录扫描计划的排期状态；键为 taskId 的字符串形式。</summary>
    public Dictionary<string, ScheduledScanState> ScheduledScans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// still_changing 的跨主循环轮次重试记录；键为 BackupScanResult.CandidateKey。
    /// 见 AgentWorker.SubmitScansWithRestabilizeAsync / RetryPendingRestabilizeScansAsync：
    /// 等待预算（maxStabilityWaitSeconds）就是靠这份记录跨越多轮主循环兑现的，
    /// 不能在主循环里同步 sleep，否则心跳会停摆。
    /// </summary>
    public Dictionary<string, RestabilizeEntry> PendingRestabilizeScans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 下一次扫描必须跳过快速指纹基线、强制走全量哈希的任务（D12）。键为 taskId 的字符串形式。
    ///
    /// 触发条件是「服务端说这个候选没了」（CANDIDATE_NOT_FOUND / CANDIDATE_FILES_MISSING）：
    /// 本地候选被剪枝掉、或清单文件损坏之后，这份备份必须重建候选才能再传。
    /// 而重建的唯一入口是扫描，扫描第一件事就是拿快速指纹和服务端下发的基线比——
    /// 源文件没变，指纹当然命中，于是直接回 no_new_backup，候选永远重建不出来。
    /// 现场表现是这份备份再也传不上去，而界面上写的是「没有新备份」。
    /// </summary>
    public List<string> ForceFullScanTaskIds { get; set; } = [];
}

/// <summary>
/// 单条 still_changing 结果的重试记录。FirstAttemptAtUtc 用来判断是否已经超出
/// maxStabilityWaitSeconds 预算，NextRetryAtUtc 用来判断本轮主循环是否该重新扫描。
/// </summary>
public sealed class RestabilizeEntry
{
    public Guid TaskId { get; set; }

    /// <summary>触发这次扫描的指令 ID；计划扫描（无指令触发）为 null。</summary>
    public Guid? CommandId { get; set; }

    public DateTime FirstAttemptAtUtc { get; set; }

    public DateTime NextRetryAtUtc { get; set; }

    public int AttemptCount { get; set; }
}

/// <summary>
/// 单个任务的扫描计划排期。存的是「下一次该在什么时候跑」而不是「上一次什么时候跑的」：
/// Agent 重启后要能直接判断是否到点，不必反推。
/// </summary>
public sealed class ScheduledScanState
{
    /// <summary>排期依据的 cron 原文；与下发配置不一致时重新排期。</summary>
    public string Cron { get; set; } = string.Empty;

    /// <summary>排期依据的时区 ID；同上。</summary>
    public string? TimeZone { get; set; }

    public DateTime NextDueAtUtc { get; set; }

    public DateTime? LastRunAtUtc { get; set; }
}

/// <summary>
/// 候选备份集在本地的摘要（审计 F-08）。
///
/// 文件清单已经挪进 <see cref="AgentCandidateFileStore"/>，这里只留摘要。
/// 原先清单就躺在这个对象里，而它随 state.json 一起被反复深拷贝和全量落盘——
/// 一个 5 万文件的任务光这一份就是 10MB 量级的 JSON。
/// </summary>
public sealed class LocalCandidateState
{
    public Guid? CandidateBackupSetId { get; set; }
    public Guid TaskId { get; set; }
    public string CandidateKey { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// 仅用于读取旧版本 state.json 的迁移入口（审计 F-08）。
    ///
    /// 老版本把整份清单写在这里。升级后必须能把它读出来搬进 AgentCandidateFileStore，
    /// 否则所有候选丢失、已通过预检的备份要重新扫一遍——一个 5 万文件的任务
    /// 重扫一次是几十分钟的全量 SHA-256。
    /// 迁移完成后立即置空，之后写出的 state.json 不再含这个字段。
    /// 全网 Agent 都升过一遍之后可以删掉它。
    /// </summary>
    public List<LocalCandidateFile>? Files { get; set; }
}

public sealed class LocalCandidateFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class AgentStateStore
{
    private const int MaxSeenItems = 500;

    /// <summary>候选条目总量上限（审计 F-08），按 UpdatedAt 淘汰最旧的</summary>
    private const int MaxCandidates = 200;

    /// <summary>
    /// 剪枝的保护窗口（D12）：这段时间内更新过的候选不按总量淘汰。
    /// 7 天足够覆盖「扫出来了但一直没传上去」的各种情况（客户端离线、上传排队、
    /// 管理员暂停后忘了恢复），而候选被剪掉的代价是那份备份彻底传不上去。
    /// </summary>
    private static readonly TimeSpan RecentCandidateWindow = TimeSpan.FromDays(7);
    private static readonly byte[] ProtectionEntropy = SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes("BackupMonitor.Agent.State.v1"));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AgentOptions _options;
    private readonly ILogger<AgentStateStore> _logger;
    private readonly object _sync = new();
    private readonly string _statePath;
    private AgentState _state;

    public AgentStateStore(IOptions<AgentOptions> options, ILogger<AgentStateStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        SecureFileSystem.CreateDirectory(_options.ExpandedDataDirectory, _options.EnforceAcl);
        _statePath = Path.Combine(_options.ExpandedDataDirectory, "state.json");
        _state = LoadState();
    }

    public AgentState Snapshot()
    {
        lock (_sync)
        {
            return JsonSerializer.Deserialize<AgentState>(JsonSerializer.Serialize(_state, JsonOptions), JsonOptions) ?? new();
        }
    }

    public Guid? ClientId
    {
        get
        {
            lock (_sync)
                return _state.ClientId;
        }
    }

    public string MachineId
    {
        get
        {
            lock (_sync)
                return _state.MachineId;
        }
    }

    public long ConfigVersion
    {
        get
        {
            lock (_sync)
                return _state.ConfigVersion;
        }
    }

    public string? ServerInstanceId
    {
        get
        {
            lock (_sync)
                return string.IsNullOrWhiteSpace(_state.ServerInstanceId) ? null : _state.ServerInstanceId;
        }
    }

    public string? ServerUrlOverride
    {
        get
        {
            lock (_sync)
                return string.IsNullOrWhiteSpace(_state.ServerUrlOverride) ? null : _state.ServerUrlOverride;
        }
    }

    public DateTime? CertificateExpiresAt
    {
        get
        {
            lock (_sync)
                return _state.CertificateExpiresAt;
        }
    }

    public void Update(Action<AgentState> update)
    {
        lock (_sync)
        {
            update(_state);
            SaveUnsafe();
        }
    }

    /// <summary>
    /// 丢弃服务端身份，保留机器指纹和身份私钥（方案 B 的自愈落点）。
    ///
    /// 保留 MachineId 是这条路径能走通的关键：服务端就是靠同一个 machineId 认出
    /// 「这是原来那台机器重新来登记」，从而把已判离线的旧身份让位给新申请
    /// （AgentRegistrationService.SupersedeOfflineClientAsync）。换一个 machineId
    /// 就变成了一台全新机器，服务端上会多出一条僵尸记录，旧记录永远不会被清掉。
    ///
    /// 保留私钥则是为了不让「自愈」变成「换钥匙」：私钥重新生成一次，
    /// DPAPI 保护数据、公钥、证书链全要跟着换一遍，而这次失联的原因跟私钥无关。
    ///
    /// ConfigVersion 必须归零：新服务端（换了空库）的配置版本从头开始，
    /// 留着旧版本号会让心跳里的 RequiredConfigVersion 永远不大于本地值，
    /// Agent 就此抱着一份属于旧身份、签名也验不过的配置不放，任务全都跑不起来。
    /// SeenCommandNonces 反过来要留着——它是重放防护的窗口，清掉只会放宽安全边界。
    /// </summary>
    public void ClearIdentity(string reason)
    {
        lock (_sync)
        {
            _state.ClientId = null;
            _state.RegistrationId = null;
            _state.ProtectedCertificatePfx = null;
            _state.CertificateThumbprint = null;
            _state.CertificateExpiresAt = null;
            _state.ConfigVersion = 0;
            _state.LastError = reason;
            SaveUnsafe();
        }
    }

    /// <summary>
    /// 记下「这个任务下一次扫描必须走全量哈希」（D12）。
    ///
    /// 服务端说候选没了（CANDIDATE_NOT_FOUND / CANDIDATE_FILES_MISSING）时调用。
    /// 不记的话，下一轮扫描的快速指纹会命中服务端下发的基线 → 直接回 no_new_backup
    /// → 候选永远重建不出来 → 这份备份再也传不上去，而界面上写的是「没有新备份」。
    /// </summary>
    public void RequestFullScan(Guid taskId)
    {
        lock (_sync)
        {
            var key = taskId.ToString();
            if (_state.ForceFullScanTaskIds.Contains(key, StringComparer.OrdinalIgnoreCase))
                return;

            _state.ForceFullScanTaskIds.Add(key);
            SaveUnsafe();
        }
    }

    /// <summary>
    /// 取出并清掉「下一次强制全量」的标记。取走即消费：全量扫一遍之后候选就重建好了，
    /// 标记留着只会让接下来每一轮都白读一遍整份备份。
    /// </summary>
    public bool ConsumeFullScanRequest(Guid taskId)
    {
        lock (_sync)
        {
            var key = taskId.ToString();
            var index = _state.ForceFullScanTaskIds.FindIndex(
                id => string.Equals(id, key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            _state.ForceFullScanTaskIds.RemoveAt(index);
            SaveUnsafe();
            return true;
        }
    }

    /// <summary>
    /// 淘汰不再属于任何在线任务的候选，并返回要删清单文件的 candidateKey（审计 F-08）。
    ///
    /// Candidates 原先只写不删——ScheduledScans 和 PendingRestabilizeScans 都有清理僵尸条目的
    /// 逻辑，唯独它没有，任务删掉之后那些候选还留在文件里，state.json 只增不减。
    ///
    /// 收集在锁里做、删文件在锁外做：删一批文件是几十次同步 IO，
    /// 攥着这把锁做等于把三条循环全停在那里。
    /// </summary>
    public IReadOnlyList<string> PruneCandidates(IReadOnlySet<Guid> liveTaskIds, int maxEntries = MaxCandidates)
    {
        lock (_sync)
        {
            var removed = _state.Candidates
                .Where(kv => !liveTaskIds.Contains(kv.Value.TaskId))
                .Select(kv => kv.Key)
                .ToList();

            // 再加一道总量闸：CandidateKey 里含有状态段，同一个任务反复扫描会
            // 不断产生新键，光靠「任务还在不在」是收敛不了的。按 UpdatedAt 淘汰最旧的。
            //
            // 但最近更新过的一律不动，哪怕总量超了（D12）。只按总量截断的话，
            // 一台多账套机器一轮扫描就能产出十几个候选，把还没来得及上传的那几个挤掉——
            // 而候选一旦被剪掉，服务端下发的上传指令就会撞 CANDIDATE_NOT_FOUND。
            // 保护窗口取「够跑完一次上传」的量级：候选是这次备份唯一的凭据，
            // 让 state.json 大一点，远好过让一份刚扫出来的备份传不上去。
            var protectedSince = DateTime.UtcNow - RecentCandidateWindow;
            var survivors = _state.Candidates
                .Where(kv => liveTaskIds.Contains(kv.Value.TaskId))
                .OrderByDescending(kv => kv.Value.UpdatedAt)
                .ToList();
            removed.AddRange(survivors
                .Skip(maxEntries)
                .Where(kv => kv.Value.UpdatedAt < protectedSince)
                .Select(kv => kv.Key));

            if (removed.Count == 0)
                return [];

            foreach (var key in removed)
                _state.Candidates.Remove(key);
            SaveUnsafe();
            return removed;
        }
    }

    /// <summary>
    /// 把老格式 state.json 里内嵌的文件清单搬进独立存储（审计 F-08）。
    /// 返回搬走的候选数量。搬完立即重写一次 state.json，之后就是新格式了。
    /// </summary>
    public int MigrateEmbeddedCandidateFiles(AgentCandidateFileStore fileStore)
    {
        lock (_sync)
        {
            var pending = _state.Candidates
                .Where(kv => kv.Value.Files is { Count: > 0 })
                .ToList();
            if (pending.Count == 0)
                return 0;

            foreach (var (key, candidate) in pending)
            {
                fileStore.Save(key, candidate.Files!);
                candidate.Files = null;
            }

            SaveUnsafe();
            return pending.Count;
        }
    }

    /// <summary>
    /// 客户端证书的私钥存放方式。
    ///
    /// Windows 上不能用 EphemeralKeySet：mTLS 的客户端认证走 Schannel，而 Schannel 只能使用
    /// 密钥容器里的私钥，拿不到纯内存的 ephemeral 密钥。表现是握手阶段
    /// AcquireCredentialsHandle 返回 SEC_E_NO_CREDENTIALS(0x8009030E)，.NET 把它翻译成
    /// 「platform does not support ephemeral keys」——请求根本发不出去，服务端日志里
    /// 连一行都不会有。注册阶段是匿名端点、不出示证书，所以能过；一旦装上证书开始心跳就必然失败。
    ///
    /// 用 PersistKeySet 而不是 MachineKeySet，与 Kestrel 侧的
    /// <see cref="BackupMonitor.Api.Bootstrap.ServerCertificateFactory.LoadForKestrel"/> 保持一致：
    /// 密钥落在服务账户自己的存储里，LocalSystem 和交互式调试都能用；强制 MachineKeySet 会让
    /// 非管理员身份运行时直接失败。
    /// </summary>
    private static X509KeyStorageFlags ClientCertificateStorageFlags =>
        OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.PersistKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

    public X509Certificate2? LoadCertificate()
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_state.ProtectedCertificatePfx))
                return null;

            try
            {
                var pfx = UnprotectForRead(
                    Convert.FromBase64String(_state.ProtectedCertificatePfx),
                    out var legacyProtection);
                if (legacyProtection)
                {
                    _state.ProtectedCertificatePfx = Convert.ToBase64String(ProtectBytes(pfx));
                    SaveUnsafe();
                }
                return new X509Certificate2(pfx, string.Empty, ClientCertificateStorageFlags);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "无法读取 Agent 客户端证书");
                return null;
            }
        }
    }

    public RSA GetOrCreatePrivateKey()
    {
        lock (_sync)
        {
            var key = RSA.Create();
            if (!string.IsNullOrWhiteSpace(_state.ProtectedPrivateKey))
            {
                try
                {
                    var bytes = UnprotectForRead(
                        Convert.FromBase64String(_state.ProtectedPrivateKey),
                        out var legacyProtection);
                    key.ImportPkcs8PrivateKey(bytes, out _);
                    if (legacyProtection)
                    {
                        _state.ProtectedPrivateKey = Convert.ToBase64String(ProtectBytes(bytes));
                        SaveUnsafe();
                    }
                    return key;
                }
                catch (Exception ex)
                {
                    key.Dispose();
                    _logger.LogWarning(ex, "Agent 私钥损坏，将重新生成身份密钥");
                }
            }

            key.Dispose();
            key = RSA.Create(2048);
            _state.PublicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            _state.ProtectedPrivateKey = Convert.ToBase64String(ProtectBytes(key.ExportPkcs8PrivateKey()));
            SaveUnsafe();
            return key;
        }
    }

    public string GetOrCreateMachineId()
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_state.MachineId))
                return _state.MachineId;

            var machineGuid = string.Empty;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography");
                machineGuid = key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取 Windows MachineGuid 失败，将使用机器名生成 Agent 身份");
            }

            var source = $"{machineGuid}|{Environment.MachineName}|{Environment.OSVersion.VersionString}";
            _state.MachineId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
            SaveUnsafe();
            return _state.MachineId;
        }
    }

    private AgentState LoadState()
    {
        // 「文件不存在」是首次运行，正常路径；「文件存在却读不出来」是异常，两者必须分开。
        if (!File.Exists(_statePath))
            return new();

        try
        {
            var state = JsonSerializer.Deserialize<AgentState>(File.ReadAllText(_statePath), JsonOptions);
            if (state is not null)
                return state;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Agent 状态文件损坏：{Path}", _statePath);
        }

        // 状态文件存在却读不出来时，绝不能返回空状态继续跑。
        // 空状态里没有 ClientId，Agent 会当成「没注册过」重新登记——
        // 于是这台机器在管理端变成一台待审批的新客户端、原客户端转离线、
        // 它名下所有任务停摆，而没有任何一条告警在说「某台机器丢了身份」。
        // 这里抛异常：LoadState 在构造函数里，AgentStateStore 是单例且随宿主一起解析，
        // 于是 Windows 服务直接启动失败并写事件日志——这是能让人看见的失败。
        // 坏文件刻意**原地保留**、不改名：ClientId 就在这个文件里，
        // 改名留档看着更整洁，但也让人更容易顺手把「那个 .corrupt 文件」删掉重装，
        // 那一删身份就真的没了。留在原地，下一次启动仍然以同样的方式失败，直到有人真的处理它。
        throw new InvalidOperationException(
            $"Agent 状态文件损坏，无法读取：{_statePath}。"
            + "这个文件里存着本机的 ClientId 与证书引用，服务已拒绝以空状态启动"
            + "（否则本机会重新登记成一台新的待审批客户端，原客户端转离线）。"
            + "请从备份恢复该文件；确认这台机器需要重新登记时，手工删除它再启动服务。");
    }

    private void SaveUnsafe()
    {
        if (_state.SeenNotificationIds.Count > MaxSeenItems)
            _state.SeenNotificationIds = _state.SeenNotificationIds.TakeLast(MaxSeenItems).ToList();
        if (_state.SeenCommandNonces.Count > MaxSeenItems)
            _state.SeenCommandNonces = _state.SeenCommandNonces.TakeLast(MaxSeenItems).ToList();

        var tempPath = _statePath + ".tmp";
        var payload = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_state, JsonOptions));

        // 先建空文件、设 ACL，再写内容，最后刷盘——三步的顺序都不能变：
        // ACL 要在内容写进去之前生效，而刷盘必须在改名之前完成。
        // File.Move 保证的是改名这一步原子，它不保证临时文件的内容已经落盘；
        // 少了 Flush(true)，掉电后拿到的是一个名字正确、内容为空或半截的 state.json，
        // 而这样一个文件正是 LoadState 里那条「读不出来」的路径要处理的东西。
        using (File.Create(tempPath))
        {
        }
        SecureFileSystem.ApplyFileAcl(tempPath, _options.EnforceAcl);

        using (var fs = new FileStream(tempPath, FileMode.Truncate, FileAccess.Write, FileShare.None))
        {
            fs.Write(payload, 0, payload.Length);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, _statePath, true);
        SecureFileSystem.ApplyFileAcl(_statePath, _options.EnforceAcl);
    }

    internal static byte[] ProtectBytes(byte[] bytes) =>
        System.Security.Cryptography.ProtectedData.Protect(bytes, ProtectionEntropy, DataProtectionScope.LocalMachine);

    internal static byte[] UnprotectBytes(byte[] bytes) =>
        System.Security.Cryptography.ProtectedData.Unprotect(bytes, ProtectionEntropy, DataProtectionScope.LocalMachine);

    private static byte[] UnprotectForRead(byte[] bytes, out bool legacyProtection)
    {
        try
        {
            legacyProtection = false;
            return UnprotectBytes(bytes);
        }
        catch (CryptographicException)
        {
            // 仅为旧版本 state.json 提供一次性迁移；读取成功后立即用固定 entropy 重写。
            legacyProtection = true;
            return System.Security.Cryptography.ProtectedData.Unprotect(
                bytes, null, DataProtectionScope.LocalMachine);
        }
    }
}
