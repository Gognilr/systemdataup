using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BackupMonitor.Shared.Models.Admin;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Scheduling;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

public sealed class AgentWorker : BackgroundService
{
    private static readonly string[] SupportedCommands =
    [
        "precheck_task", "precheck_all", "upload_candidate", "upload_latest",
        "rescan", "rehash", "sync_config", "refresh_metrics", "upgrade_agent",
        "browse_path", "list_services"
    ];

    private readonly AgentOptions _options;
    private readonly AgentStateStore _stateStore;
    private readonly AgentTrayNotificationStore _trayNotifications;
    private readonly AgentConfigStore _configStore;
    private readonly AgentCandidateFileStore _candidateFiles;
    private readonly AgentApiClient _api;
    private readonly AgentSignatureVerifier _signatures;
    private readonly SystemProbe _probe;
    private readonly BackupScanner _scanner;
    private readonly DirectoryBrowser _browser;
    private readonly InstalledServiceProbe _serviceProbe;
    private readonly ILogger<AgentWorker> _logger;

    /// <summary>
    /// 正在执行的指令 ID（审计 B-02）。
    ///
    /// 拆循环之后它跨线程了：心跳循环读它组装 ActiveCommands，指令循环写它。
    /// 原先是 HashSet，单循环时代没问题，现在必须换成并发容器。
    /// 用 ConcurrentDictionary 当集合（值不用）是因为 .NET 里没有 ConcurrentHashSet。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _activeCommands = new();

    /// <summary>
    /// 已验签的当前配置缓存（审计 B-02）。
    ///
    /// LoadVerifiedConfig() 每次都要读文件再做一次 RSA 验签。单循环时代一轮只调几次，
    /// 拆成三条循环之后每条都要用，反复验签纯属浪费。
    /// volatile + 整体替换引用：读侧只做一次快照读，不加锁；
    /// 配置对象本身在替换之后不再被修改，因此读到的引用永远是一份自洽的完整配置。
    /// </summary>
    private volatile AgentConfigResponse? _config;

    /// <summary>
    /// SyncConfigAsync 互斥（审计 B-02）。心跳循环发现 RequiredConfigVersion 更高会调它，
    /// sync_config 指令也会调它——两边同时写 config.json 会写出半份文件。
    /// </summary>
    private readonly SemaphoreSlim _configSync = new(1, 1);

    public AgentWorker(
        IOptions<AgentOptions> options,
        AgentStateStore stateStore,
        AgentTrayNotificationStore trayNotifications,
        AgentConfigStore configStore,
        AgentCandidateFileStore candidateFiles,
        AgentApiClient api,
        AgentSignatureVerifier signatures,
        SystemProbe probe,
        BackupScanner scanner,
        DirectoryBrowser browser,
        InstalledServiceProbe serviceProbe,
        ILogger<AgentWorker> logger)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _trayNotifications = trayNotifications;
        _configStore = configStore;
        _candidateFiles = candidateFiles;
        _api = api;
        _signatures = signatures;
        _probe = probe;
        _scanner = scanner;
        _browser = browser;
        _serviceProbe = serviceProbe;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackupMonitor Agent {Version} 启动，服务器={Server}", _options.AgentVersion, _options.ServerUrl);

        // 注册与首次取证书仍然串行：没有 clientId 和客户端证书，
        // 下面三条循环里没有一条能发出任何请求。
        if (!await WaitForRegistrationAsync(stoppingToken))
        {
            _logger.LogInformation("BackupMonitor Agent 停止");
            return;
        }

        RefreshConfigCache();

        // 审计 F-08：老版本把候选文件清单内嵌在 state.json 里，升级后必须搬出来。
        // 不搬的话所有候选丢失，已通过预检的备份要重新全量 SHA-256 扫一遍。
        var migrated = _stateStore.MigrateEmbeddedCandidateFiles(_candidateFiles);
        if (migrated > 0)
            _logger.LogInformation("已将 {Count} 个候选的文件清单迁出 state.json", migrated);

        // 审计 B-02：拆成三条独立节奏。
        //
        // 原先只有一条顺序循环，心跳、指令执行、计划扫描共用它，而指令执行是 await 到底的：
        // 传一份 50GB 的备份要几个小时，这几小时里一次心跳都发不出去，
        // 服务端 SystemWatchdogWorker 按 client_offline_threshold_seconds（默认 300 秒）
        // 判离线并发 Critical 告警——每一次正经的备份上传都会在 5 分钟后
        // 把这台正在老实干活的机器判成离线，把离线巡检训练成「狼来了」然后被关掉。
        //
        // 刻意不用「在上传循环里插补心跳」代替拆循环：那是止血，
        // 扫描阶段的长哈希覆盖不到（扫一个大目录同样能超过 5 分钟），
        // 而且每加一个耗时动作就要记得插一次，迟早会漏。
        await Task.WhenAll(
            RunHeartbeatLoopAsync(stoppingToken),
            RunCommandLoopAsync(stoppingToken),
            RunScanLoopAsync(stoppingToken));

        _logger.LogInformation("BackupMonitor Agent 停止");
    }

    /// <summary>注册 + 首次证书安装，成功之前一直重试。返回 false 表示收到停机信号。</summary>
    private async Task<bool> WaitForRegistrationAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_stateStore.Snapshot().ClientId is not null)
                {
                    await RenewCertificateIfNeededAsync(ct);
                    return true;
                }

                if (!await EnsureRegisteredAsync(ct))
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 注册流程异常");
                _stateStore.Update(s => s.LastError = ex.Message);
                if (!await DelayAsync(10, ct))
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 心跳循环：按服务端下发的 heartbeat_interval_seconds 走（审计 B-09）。
    ///
    /// 原先的 delay 是 Math.Min(heartbeatDelay, CommandPollIntervalSeconds) 再 Clamp 到 2..60，
    /// 于是服务端配的 heartbeat_interval_seconds 只要大于 10 秒就完全失效——
    /// 实际心跳量是设计值的 6 倍，而管理员改那个配置根本没有任何效果。
    /// </summary>
    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delaySeconds = Math.Clamp(_options.HeartbeatIntervalSeconds, 5, 300);
            try
            {
                await RenewCertificateIfNeededAsync(ct);

                var state = _stateStore.Snapshot();
                var heartbeat = await _api.HeartbeatAsync(
                    _probe.BuildHeartbeat(state.ConfigVersion, _config, _activeCommands.Keys.ToList()), ct);

                ProcessNotifications(heartbeat.Notifications);

                if (heartbeat.CertificateRenewalRequired)
                    await RenewCertificateIfNeededAsync(ct, force: true);

                if (heartbeat.RequiredConfigVersion > state.ConfigVersion)
                    await SyncConfigAsync(ct);

                if (heartbeat.HeartbeatIntervalSeconds > 0)
                    delaySeconds = Math.Clamp(heartbeat.HeartbeatIntervalSeconds, 5, 300);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AgentApiException ex)
            {
                LogApiFailure(ex);

                // mTLS 形态下 401 意味着服务端没有收到（或不认）本次连接出示的客户端证书。
                // 本地明明装着证书却被拒，最可能的原因是连接的 TLS 身份与本地证书脱节——
                // 证书是握手时协商的，池中的旧连接不会因为本地换了证书而改变身份。
                // 重新握手即可自愈。放在心跳循环里就够了：连接池是共享的，
                // 回收一次三条循环都受益，不必每条各判一遍。
                if (ex.StatusCode == 401 && _stateStore.Snapshot().CertificateThumbprint is not null)
                    _api.RecycleConnections();

                delaySeconds = 10;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 心跳循环异常");
                _stateStore.Update(s => s.LastError = ex.Message);
                delaySeconds = 10;
            }

            if (!await DelayAsync(delaySeconds, ct))
                break;
        }
    }

    /// <summary>
    /// 指令循环：按 CommandPollIntervalSeconds 轮询，不携带任何快照（审计 B-09）。
    ///
    /// 指令之间仍然串行（MaxItems 不变）。拆的是「心跳 vs 指令」，不是「指令之间」——
    /// 并发跑多个上传会把源盘 IO 打满，那是另一个问题。
    /// </summary>
    private async Task RunCommandLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delaySeconds = Math.Clamp(_options.CommandPollIntervalSeconds, 2, 60);
            try
            {
                await ClaimAndExecuteCommandsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AgentApiException ex)
            {
                LogApiFailure(ex);
                delaySeconds = 10;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 指令循环异常");
                _stateStore.Update(s => s.LastError = ex.Message);
                delaySeconds = 10;
            }

            if (!await DelayAsync(delaySeconds, ct))
                break;
        }
    }

    /// <summary>
    /// 计划扫描循环。扫一个大目录同样可能跑几分钟，
    /// 它和指令执行分开是因为两者都是耗时动作，堵在一起就等于没拆。
    /// </summary>
    private async Task RunScanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delaySeconds = Math.Clamp(_options.CommandPollIntervalSeconds, 5, 60);
            try
            {
                var config = _config;
                await RunScheduledScansAsync(config, ct);
                await RetryPendingRestabilizeScansAsync(config, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AgentApiException ex)
            {
                LogApiFailure(ex);
                delaySeconds = 10;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 计划扫描循环异常");
                _stateStore.Update(s => s.LastError = ex.Message);
                delaySeconds = 10;
            }

            if (!await DelayAsync(delaySeconds, ct))
                break;
        }
    }

    public override void Dispose()
    {
        _configSync.Dispose();
        base.Dispose();
    }

    private void LogApiFailure(AgentApiException ex)
    {
        _logger.LogWarning("Agent API 调用失败 status={Status} code={Code}: {Message}", ex.StatusCode, ex.ErrorCode, ex.Message);
        _stateStore.Update(s => s.LastError = ex.Message);
    }

    /// <summary>循环末尾的统一等待。返回 false 表示收到停机信号。</summary>
    private static async Task<bool> DelayAsync(int seconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RenewCertificateIfNeededAsync(CancellationToken ct, bool force = false)
    {
        var expiresAt = _stateStore.CertificateExpiresAt;
        if (!force && (expiresAt is null || expiresAt > DateTime.UtcNow.AddDays(30)))
            return;

        using var key = _stateStore.GetOrCreatePrivateKey();
        var result = await _api.RenewCertificateAsync(ct);
        using var publicCertificate = X509Certificate2.CreateFromPem(result.CertificatePem);
        using var certificateWithKey = publicCertificate.CopyWithPrivateKey(key);
        var pfx = certificateWithKey.Export(X509ContentType.Pfx, string.Empty);
        _stateStore.Update(s =>
        {
            s.ProtectedCertificatePfx = Convert.ToBase64String(AgentStateStore.ProtectBytes(pfx));
            s.CertificateThumbprint = result.CertificateThumbprint;
            s.CertificateExpiresAt = result.CertificateExpiresAt;
            s.LastError = null;
        });

        var installed = _stateStore.LoadCertificate()
            ?? throw new InvalidOperationException("续签成功但本地无法加载新的客户端证书");
        _api.ReplaceCertificate(installed);
        _logger.LogInformation(
            "客户端证书续签完成 thumbprint={Thumbprint} expiresAt={ExpiresAt:O}",
            result.CertificateThumbprint,
            result.CertificateExpiresAt);
    }

    private void ProcessNotifications(IEnumerable<AgentNotificationDto>? notifications)
    {
        if (notifications is null)
            return;

        var seen = _stateStore.Snapshot().SeenNotificationIds.ToHashSet();
        var enqueued = new List<Guid>();

        foreach (var notification in notifications)
        {
            if (notification.Id == Guid.Empty || !seen.Add(notification.Id))
                continue;

            if (!_trayNotifications.TryEnqueue(notification))
            {
                seen.Remove(notification.Id);   // 没入队就不算见过，下次心跳还要再试
                continue;
            }

            enqueued.Add(notification.Id);
        }

        if (enqueued.Count == 0)
            return;

        // 审计 F-08：循环外一次 Update。原先每条通知一次，而一次心跳最多带 20 条——
        // 那就是 20 次「建临时文件 + 打 ACL + 全量写 + Move + 再打一次 ACL」。
        // 上限截断交给 SaveUnsafe 统一处理，这里不重复一份。
        _stateStore.Update(state =>
        {
            foreach (var id in enqueued)
            {
                if (!state.SeenNotificationIds.Contains(id))
                    state.SeenNotificationIds.Add(id);
            }
        });
    }

    private async Task<bool> EnsureRegisteredAsync(CancellationToken ct)
    {
        var state = _stateStore.Snapshot();
        using var key = _stateStore.GetOrCreatePrivateKey();
        var publicKey = state.PublicKeySpki;
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            _stateStore.Update(s => s.PublicKeySpki = publicKey);
        }

        if (state.RegistrationId is null)
        {
            var registrationToken = _options.AutomaticEnrollment ? null : ReadRegistrationToken();
            if (!_options.AutomaticEnrollment && string.IsNullOrWhiteSpace(registrationToken))
            {
                _logger.LogError("Agent 尚未注册：请提供受 ACL 保护的 registration-token.txt");
                return false;
            }

            var request = new SubmitRegistrationRequest
            {
                RegistrationToken = registrationToken,
                MachineId = _stateStore.GetOrCreateMachineId(),
                Hostname = Environment.MachineName,
                DisplayName = string.IsNullOrWhiteSpace(_options.DisplayName) ? Environment.MachineName : _options.DisplayName,
                OsName = "Windows",
                OsVersion = Environment.OSVersion.VersionString,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                AgentVersion = _options.AgentVersion,
                IpAddresses = _probe.GetIpAddresses(),
                PublicKey = publicKey
            };
            var submitted = await _api.SubmitRegistrationAsync(request, ct);
            if (!string.IsNullOrWhiteSpace(registrationToken))
                ClearRegistrationToken();
            _stateStore.Update(s =>
            {
                s.RegistrationId = submitted.RegistrationId;
                s.LastError = null;
            });
            _logger.LogInformation("Agent 注册申请已提交 registrationId={RegistrationId} status={Status}", submitted.RegistrationId, submitted.Status);
            return false;
        }

        var result = await _api.GetRegistrationResultAsync(state.RegistrationId.Value, ct);
        if (string.Equals(result.Status, "pending_approval", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Agent 等待服务器审批 registrationId={RegistrationId}", state.RegistrationId);
            return false;
        }

        if (!string.Equals(result.Status, "approved", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(result.CertificatePem))
        {
            _stateStore.Update(s => s.LastError = result.RejectionReason ?? $"注册状态：{result.Status}");
            _logger.LogError("Agent 注册未通过 status={Status} reason={Reason}", result.Status, result.RejectionReason);
            return false;
        }

        using var publicCertificate = X509Certificate2.CreateFromPem(result.CertificatePem);
        using var certificateWithKey = publicCertificate.CopyWithPrivateKey(key);
        var pfx = certificateWithKey.Export(X509ContentType.Pfx, string.Empty);
        _stateStore.Update(s =>
        {
            s.ClientId = result.ClientId ?? result.RegistrationId;
            s.ProtectedCertificatePfx = Convert.ToBase64String(AgentStateStore.ProtectBytes(pfx));
            s.CertificateThumbprint = result.CertificateThumbprint ?? certificateWithKey.Thumbprint;
            s.CertificateExpiresAt = result.CertificateExpiresAt;
            s.LastError = null;
        });
        _api.AttachCertificate(_stateStore.LoadCertificate()!);
        _logger.LogInformation("Agent 已完成审批并安装客户端证书 clientId={ClientId}", result.ClientId);
        return true;
    }

    /// <summary>
    /// 拉取并落地服务端配置。
    ///
    /// 审计 B-02：整段用信号量互斥。心跳循环和 sync_config 指令都会调它，
    /// 两边同时写 config.json 会写出半份文件，而配置一旦坏掉 Agent 就什么都干不了。
    /// </summary>
    private async Task<AgentConfigResponse?> SyncConfigAsync(CancellationToken ct)
    {
        await _configSync.WaitAsync(ct);
        try
        {
            var version = _stateStore.Snapshot().ConfigVersion;
            var config = await _api.GetConfigAsync(version, ct);
            if (config is null)
                return RefreshConfigCache();   // 304：本地这份就是最新的

            var clientId = _stateStore.Snapshot().ClientId;
            if (clientId is null || !_signatures.VerifyConfig(config, clientId.Value))
                throw new AgentApiException(498, "服务端配置签名校验失败", "CONFIG_SIGNATURE_INVALID");

            _configStore.Save(config);
            _stateStore.Update(s =>
            {
                s.ConfigVersion = config.Version;
                s.LastError = null;
            });

            // 整体替换引用：读侧看到的要么是旧的完整配置，要么是新的完整配置，
            // 不存在「一半新一半旧」的中间态。
            _config = config;
            _logger.LogInformation("Agent 配置已同步 version={Version} tasks={Tasks}", config.Version, config.Tasks.Count);
            return config;
        }
        finally
        {
            _configSync.Release();
        }
    }

    private async Task ClaimAndExecuteCommandsAsync(CancellationToken ct)
    {
        var response = await _api.ClaimCommandsAsync(new ClaimCommandsRequest
        {
            MaxItems = 3,
            SupportedCommandTypes = SupportedCommands.ToList()
        }, ct);

        foreach (var command in response.Commands)
        {
            _activeCommands[command.Id] = 0;
            try
            {
                await ExecuteCommandAsync(command, ct);
            }
            finally
            {
                _activeCommands.TryRemove(command.Id, out _);
            }
        }
    }

    private async Task ExecuteCommandAsync(CommandDto command, CancellationToken ct)
    {
        var state = _stateStore.Snapshot();
        if (state.ClientId is null || !_signatures.VerifyCommand(command, state.ClientId.Value))
        {
            await _api.ReportCompletedAsync(command.Id, new CommandCompletedRequest
            {
                Success = false,
                ResultCode = "COMMAND_SIGNATURE_INVALID",
                ResultMessage = "指令签名、客户端绑定或有效期校验失败"
            }, ct);
            return;
        }

        if (state.SeenCommandNonces.Any(nonce => string.Equals(nonce, command.Nonce, StringComparison.Ordinal)))
        {
            await _api.ReportCompletedAsync(command.Id, new CommandCompletedRequest
            {
                Success = false,
                ResultCode = "COMMAND_REPLAYED",
                ResultMessage = "指令 nonce 已经执行过"
            }, ct);
            return;
        }

        _stateStore.Update(s =>
        {
            if (!s.SeenCommandNonces.Any(nonce => string.Equals(nonce, command.Nonce, StringComparison.Ordinal)))
                s.SeenCommandNonces.Add(command.Nonce);
            if (s.SeenCommandNonces.Count > 500)
                s.SeenCommandNonces = s.SeenCommandNonces.TakeLast(500).ToList();
        });

        await _api.ReportStartedAsync(command.Id, ct);
        try
        {
            var result = command.Type switch
            {
                "precheck_task" => IsTestOnly(command)
                    ? await ExecuteRecognitionTestAsync(command.TaskId, ct)
                    : await ExecutePrecheckAsync(command.TaskId, command.Id, ct),
                "precheck_all" => await ExecutePrecheckAllAsync(command.Id, ct),
                "upload_candidate" or "upload_latest" => await ExecuteUploadAsync(command, ct),
                "rescan" or "rehash" => await ExecutePrecheckAsync(command.TaskId, command.Id, ct),
                "sync_config" => await SyncConfigAsync(ct) is not null
                    ? CommandResult.FromSuccess("CONFIG_SYNCED", "配置已同步")
                    : CommandResult.FromFailure("CONFIG_NOT_CHANGED", "配置未变化"),
                "refresh_metrics" => CommandResult.FromSuccess("OK", "指标将在下一次心跳上报"),
                "upgrade_agent" => await StageUpgradeAsync(command, ct),
                "browse_path" => ExecuteBrowse(command, ct),
                "list_services" => ExecuteListServices(command, ct),
                _ => CommandResult.FromFailure("COMMAND_UNSUPPORTED", $"不支持的指令类型：{command.Type}")
            };

            await _api.ReportCompletedAsync(command.Id, new CommandCompletedRequest
            {
                Success = result.Success,
                ResultCode = result.Code,
                ResultMessage = result.Message,
                Result = result.ResultJson
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent 指令执行失败 commandId={CommandId} type={Type}", command.Id, command.Type);
            await _api.ReportCompletedAsync(command.Id, new CommandCompletedRequest
            {
                Success = false,
                ResultCode = ex is AgentApiException api ? api.ErrorCode ?? "API_ERROR" : "AGENT_ERROR",
                ResultMessage = ex.Message
            }, ct);
        }
    }

    private async Task<CommandResult> ExecutePrecheckAllAsync(Guid commandId, CancellationToken ct)
    {
        var config = LoadVerifiedConfig();
        if (config is null)
            config = await SyncConfigAsync(ct);
        if (config is null)
            return CommandResult.FromFailure("CONFIG_MISSING", "本地没有可用配置");

        var executed = 0;
        foreach (var task in config.Tasks.Where(t => t.Enabled))
        {
            var result = await SubmitScanResultsAsync(task, commandId, ct);
            if (result > 0)
                executed += result;
        }

        return CommandResult.FromSuccess("PRECHECK_SUBMITTED", $"已提交 {executed} 个预检结果");
    }

    /// <summary>
    /// 指令参数里是否带 testOnly。识别测试走的是同一种指令，
    /// 区别在于只看不写——绝不能让"试一下"产生候选备份集。
    /// </summary>
    private static bool IsTestOnly(CommandDto command)
    {
        if (string.IsNullOrWhiteSpace(command.Payload))
            return false;

        try
        {
            using var document = JsonDocument.Parse(command.Payload);
            return document.RootElement.TryGetProperty("testOnly", out var value)
                   && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 识别测试：按当前配置扫一遍，把"会识别出什么"原样报回服务端，不提交预检结果。
    ///
    /// 这是给人看的——填完源路径和识别规则，先看看到底会挑中哪些文件、判定通不通过，
    /// 而不是保存完等到某天需要恢复时才发现规则写错了。
    /// 因此结果走指令返回值（ResultPayload），不进候选备份集，也不改任务的扫描时间。
    /// </summary>
    private async Task<CommandResult> ExecuteRecognitionTestAsync(Guid? taskId, CancellationToken ct)
    {
        if (taskId is null)
            return CommandResult.FromFailure("TASK_ID_REQUIRED", "识别测试指令缺少 taskId");

        var config = LoadVerifiedConfig() ?? await SyncConfigAsync(ct);
        var task = config?.Tasks.FirstOrDefault(t => t.TaskId == taskId.Value);
        if (task is null)
            return CommandResult.FromFailure("TASK_NOT_FOUND", $"本地配置不存在任务：{taskId}");

        var scans = await ScanWithSyncRestabilizeAsync(task, ct);
        var payload = JsonSerializer.Serialize(new
        {
            sourcePath = task.SourcePath,
            recognizerType = task.RecognizerType,
            scannedAt = DateTime.UtcNow,
            results = scans.Select(scan => new
            {
                sourceRoot = scan.SourceRoot,
                businessUnit = scan.BusinessUnit?.DisplayName,
                status = scan.Status,
                failureCode = scan.FailureCode,
                failureMessage = scan.FailureMessage,
                backupBusinessTime = scan.BackupBusinessTime,
                totalFiles = scan.Files.Count,
                totalBytes = scan.Files.Sum(f => f.SizeBytes),
                // 只回前 50 个文件：识别测试是给人看的，一屏看不完的清单没有意义，
                // 而完整清单可能有几万条，塞进指令结果里会把 commands 表撑坏。
                // 属性名必须显式写成小驼峰。匿名对象的简写形式（file.RelativePath）
                // 序列化出来是 PascalCase，而界面读的是 relativePath——
                // 结果是文件清单整列显示 undefined，且不报任何错。
                files = scan.Files.Take(50).Select(file => new
                {
                    relativePath = file.RelativePath,
                    sizeBytes = file.SizeBytes,
                    lastModifiedAt = file.LastModifiedAt,
                    isRequired = file.IsRequired
                })
            })
        });

        var passed = scans.Count(scan => scan.Status == "passed");
        return new CommandResult(
            true,
            "RECOGNITION_TESTED",
            $"扫描到 {scans.Count} 个识别单元，其中 {passed} 个可通过预检",
            payload);
    }

    /// <summary>
    /// 识别测试专用的稳定等待：人在对话框前等结果，不能像计划扫描那样跨主循环轮次重试，
    /// 但也不能对着 maxStabilityWaitSeconds（可能配了 10 分钟）傻等。
    ///
    /// 上限是 15 秒，不是 60 秒：界面等一条指令的死线就是 60 秒，等待窗口和死线一样长
    /// 等于保证超时——人什么都看不到，只能猜是不是离线了。等不到就如实回报
    /// still_changing，「文件还在写」本身就是一条有用的结论，比一句「等待超时」强。
    /// </summary>
    private async Task<List<BackupScanResult>> ScanWithSyncRestabilizeAsync(AgentTaskConfigDto task, CancellationToken ct)
    {
        var scans = await _scanner.ScanForTestAsync(task, ct);
        var maxWaitSeconds = Math.Min(Math.Max(task.MaxStabilityWaitSeconds, 0), 15);
        if (maxWaitSeconds <= 0 || !scans.Any(s => s.Status == "still_changing"))
            return scans;

        var stabilityDelaySeconds = Math.Max(1, task.StabilityIntervalSeconds);
        var stopwatch = Stopwatch.StartNew();
        while (scans.Any(s => s.Status == "still_changing"))
        {
            var remaining = maxWaitSeconds - stopwatch.Elapsed.TotalSeconds;
            if (remaining <= 0)
                break;

            var delaySeconds = Math.Min(stabilityDelaySeconds, remaining);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            scans = await _scanner.ScanForTestAsync(task, ct);
        }

        if (stopwatch.Elapsed.TotalSeconds >= maxWaitSeconds)
        {
            var waited = (int)stopwatch.Elapsed.TotalSeconds;
            scans = scans
                .Select(scan => scan.Status == "still_changing"
                    ? WithFailureMessage(scan, $"等待了 {waited} 秒后仍在变化")
                    : scan)
                .ToList();
        }

        return scans;
    }

    /// <summary>
    /// 目录浏览：抓一棵只含元数据的目录树回给服务端，供建任务向导使用。
    ///
    /// 同步执行是有意的——枚举本身是 IO 密集但无需并发，而 DirectoryBrowser 内部
    /// 已经用条目数/深度/耗时三重限额兜住了最坏情况。
    /// </summary>
    private CommandResult ExecuteBrowse(CommandDto command, CancellationToken ct)
    {
        BrowsePathCommandPayload payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(command.Payload)
                ? new BrowsePathCommandPayload()
                : JsonSerializer.Deserialize<BrowsePathCommandPayload>(command.Payload, BrowseJsonOptions)
                  ?? new BrowsePathCommandPayload();
        }
        catch (JsonException ex)
        {
            return CommandResult.FromFailure("BROWSE_PAYLOAD_INVALID", $"浏览指令参数无法解析：{ex.Message}");
        }

        try
        {
            var snapshot = _browser.Browse(payload, ct);
            var json = JsonSerializer.Serialize(snapshot, BrowseJsonOptions);
            var message = snapshot.IsDriveList
                ? $"返回 {snapshot.Entries.Count} 个磁盘"
                : $"返回 {snapshot.TotalEntries} 个条目{(snapshot.Truncated ? "（已截断）" : "")}";
            return new CommandResult(true, "BROWSE_OK", message, json);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CommandResult.FromFailure("BROWSE_FORBIDDEN", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return CommandResult.FromFailure("BROWSE_PATH_NOT_FOUND", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "目录浏览失败 path={Path}", payload.Path);
            return CommandResult.FromFailure("BROWSE_FAILED", $"目录浏览失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 列出本机安装的 Windows 服务，供服务端配置关键服务监控时挑选。
    /// 只读，不对任何服务做启停。
    /// </summary>
    private CommandResult ExecuteListServices(CommandDto command, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return CommandResult.FromFailure("NOT_SUPPORTED", "只有 Windows 客户端支持列出服务");

        var includeStopped = true;
        if (!string.IsNullOrWhiteSpace(command.Payload))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<ListServicesCommandPayload>(command.Payload, BrowseJsonOptions);
                includeStopped = payload?.IncludeStopped ?? true;
            }
            catch (JsonException)
            {
                // 参数解析不了就按默认来：这条指令只是列个清单，没必要因为参数瑕疵整个失败。
            }
        }

        try
        {
            var result = _serviceProbe.List(includeStopped, ct);
            return new CommandResult(
                true,
                "SERVICES_LISTED",
                $"返回 {result.Services.Count} 个服务",
                JsonSerializer.Serialize(result, BrowseJsonOptions));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "列出 Windows 服务失败");
            return CommandResult.FromFailure("LIST_SERVICES_FAILED", ex.Message);
        }
    }

    /// <summary>浏览结果用小驼峰，与服务端 DTO 的反序列化约定一致。</summary>
    private static readonly JsonSerializerOptions BrowseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private async Task<CommandResult> ExecutePrecheckAsync(Guid? taskId, Guid commandId, CancellationToken ct)
    {
        if (taskId is null)
            return CommandResult.FromFailure("TASK_ID_REQUIRED", "预检指令缺少 taskId");
        var config = LoadVerifiedConfig() ?? await SyncConfigAsync(ct);
        var task = config?.Tasks.FirstOrDefault(t => t.TaskId == taskId.Value);
        if (task is null)
            return CommandResult.FromFailure("TASK_NOT_FOUND", $"本地配置不存在任务：{taskId}");

        var count = await SubmitScanResultsAsync(task, commandId, ct);
        return CommandResult.FromSuccess("PRECHECK_SUBMITTED", $"已提交 {count} 个预检结果");
    }

    /// <summary>
    /// 扫描并上报预检结果。commandId 为 null 表示这次扫描不是由服务端指令触发的
    /// （本地扫描计划自行触发），服务端据此跳过指令幂等与结果回填。
    /// </summary>
    private async Task<int> SubmitScanResultsAsync(AgentTaskConfigDto task, Guid? commandId, CancellationToken ct)
    {
        var scans = await _scanner.ScanAsync(task, ct);
        return await SubmitScansWithRestabilizeAsync(task, scans, commandId, ct);
    }

    /// <summary>
    /// 提交扫描结果；still_changing 的结果套用「跨主循环轮次重试」的稳定等待预算
    /// （AgentStateStore.PendingRestabilizeScans），而不是在这里同步 sleep——
    /// 阻塞会让心跳停摆。用于计划扫描与指令预检两条路径；识别测试走的是
    /// ScanWithSyncRestabilizeAsync 的同步等待，不经过这里。
    /// </summary>
    private async Task<int> SubmitScansWithRestabilizeAsync(AgentTaskConfigDto task, List<BackupScanResult> scans, Guid? commandId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var maxWaitSeconds = task.MaxStabilityWaitSeconds;
        var stabilityDelaySeconds = Math.Max(1, task.StabilityIntervalSeconds);
        var submitted = 0;

        // 审计 F-08：开头取一次快照，循环里不再反复调 Snapshot()。
        // Snapshot() 是「序列化再反序列化」的深拷贝，state.json 里有多少东西就走多少字节；
        // 原先每个扫描结果最多要调两次，一次扫描出十几个业务单元就是几十次深拷贝。
        // 这份快照只用来读「之前有没有重试记录」，写仍然走 Update，不存在读到脏值的问题。
        var pending = _stateStore.Snapshot().PendingRestabilizeScans;

        foreach (var scan in scans)
        {
            if (scan.Status != "still_changing")
            {
                // 稳定了（或本来就是别的失败原因）：清掉可能存在的重试记录，按老流程上报。
                if (pending.ContainsKey(scan.CandidateKey))
                    _stateStore.Update(s => s.PendingRestabilizeScans.Remove(scan.CandidateKey));

                await SubmitOneScanAsync(task, scan, commandId, ct);
                submitted++;
                continue;
            }

            // 没配 maxStabilityWaitSeconds，或比稳定窗口本身还短：没有可用的重试预算，
            // 退化成老行为——扫一次就报，等下一个 cron。
            if (maxWaitSeconds <= 0 || maxWaitSeconds <= task.StabilityIntervalSeconds)
            {
                await SubmitOneScanAsync(task, scan, commandId, ct);
                submitted++;
                continue;
            }

            var entry = pending.GetValueOrDefault(scan.CandidateKey);
            if (entry is null)
            {
                var nextRetry = now.AddSeconds(stabilityDelaySeconds);
                _stateStore.Update(s => s.PendingRestabilizeScans[scan.CandidateKey] = new RestabilizeEntry
                {
                    TaskId = task.TaskId,
                    CommandId = commandId,
                    FirstAttemptAtUtc = now,
                    NextRetryAtUtc = nextRetry,
                    AttemptCount = 1
                });
                _logger.LogInformation(
                    "任务 {Task} 等待稳定，第 1 次重试，将在 {Next:O} 重新扫描 root={Root}",
                    task.Name, nextRetry, scan.SourceRoot);
                continue; // 不上报，等主循环下一轮到点重试
            }

            if (now - entry.FirstAttemptAtUtc >= TimeSpan.FromSeconds(maxWaitSeconds))
            {
                // 预算用完，认输：按 still_changing 上报，但把重试历史写进 failureMessage，
                // 否则运维看到的还是一句没有信息量的话。
                _stateStore.Update(s => s.PendingRestabilizeScans.Remove(scan.CandidateKey));
                var message = $"已在 {maxWaitSeconds} 秒内重试 {entry.AttemptCount} 次，文件仍在变化";
                await SubmitOneScanAsync(task, WithFailureMessage(scan, message), commandId, ct);
                submitted++;
                continue;
            }

            var attempt = entry.AttemptCount + 1;
            var next = now.AddSeconds(stabilityDelaySeconds);
            _stateStore.Update(s => s.PendingRestabilizeScans[scan.CandidateKey] = new RestabilizeEntry
            {
                TaskId = task.TaskId,
                CommandId = commandId ?? entry.CommandId,
                FirstAttemptAtUtc = entry.FirstAttemptAtUtc,
                NextRetryAtUtc = next,
                AttemptCount = attempt
            });
            _logger.LogInformation(
                "任务 {Task} 等待稳定，第 {Attempt} 次重试，将在 {Next:O} 重新扫描 root={Root}",
                task.Name, attempt, next, scan.SourceRoot);
        }

        return submitted;
    }

    /// <summary>
    /// 主循环每轮检查一次：PendingRestabilizeScans 里到点的任务重新扫描一遍。
    /// 等待因此发生在主循环的多次轮次之间，心跳照常，不占用工作线程同步阻塞。
    /// </summary>
    private async Task RetryPendingRestabilizeScansAsync(AgentConfigResponse? config, CancellationToken ct)
    {
        if (config is null)
            return;

        // 任务被删除或停用后，残留的重试记录要跟着清掉，否则状态文件只增不减。
        var liveTaskIds = new HashSet<Guid>(config.Tasks.Select(t => t.TaskId));
        _stateStore.Update(state =>
        {
            foreach (var stale in state.PendingRestabilizeScans
                         .Where(kv => !liveTaskIds.Contains(kv.Value.TaskId))
                         .Select(kv => kv.Key)
                         .ToList())
                state.PendingRestabilizeScans.Remove(stale);
        });

        // 审计 F-08：候选也跟着任务生命周期清理。ScheduledScans 和 PendingRestabilizeScans
        // 都早就有这段逻辑，唯独 Candidates 只写不删——任务删掉之后那些候选连同清单文件
        // 一直留着，state.json 只增不减。liveTaskIds 这里现成，直接复用。
        //
        // 删文件放在 Update 之外：PruneCandidates 在锁里只收集 key，
        // 几十次同步删文件不能攥着那把锁做，三条循环都在等它。
        foreach (var candidateKey in _stateStore.PruneCandidates(liveTaskIds))
            _candidateFiles.Delete(candidateKey);

        var now = DateTime.UtcNow;
        var dueTaskIds = _stateStore.Snapshot().PendingRestabilizeScans.Values
            .Where(e => e.NextRetryAtUtc <= now)
            .Select(e => e.TaskId)
            .Distinct()
            .ToList();

        foreach (var taskId in dueTaskIds)
        {
            ct.ThrowIfCancellationRequested();
            var task = config.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task is null)
                continue;

            // 同一个任务下多个业务单元的重试记录可能来自同一次指令触发，延用其 commandId。
            var commandId = _stateStore.Snapshot().PendingRestabilizeScans.Values
                .FirstOrDefault(e => e.TaskId == taskId)?.CommandId;

            try
            {
                var scans = await _scanner.ScanAsync(task, ct);
                var submitted = await SubmitScansWithRestabilizeAsync(task, scans, commandId, ct);
                if (submitted > 0)
                    _logger.LogInformation("等待稳定后重新扫描完成 task={Task} 已提交 {Count} 个预检结果", task.Name, submitted);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 一个任务的重试失败不影响其余任务。
                _logger.LogError(ex, "等待稳定重试扫描失败 task={Task}", task.Name);
                _stateStore.Update(state => state.LastError = ex.Message);
            }
        }
    }

    private async Task SubmitOneScanAsync(AgentTaskConfigDto task, BackupScanResult scan, Guid? commandId, CancellationToken ct)
    {
        // 试扫结果没有 SHA-256、没有 manifestHash，上报上去就是一个哈希为空的候选备份集——
        // 上传时才会发现清单对不上。识别测试的两条路径今天都不经过这里，这一刀是为了
        // 「以后有人图省事把试扫接到上报路径上」那一天：让它当场炸，而不是悄悄产生坏数据。
        if (scan.IsTestScan)
            throw new InvalidOperationException($"试扫结果不能上报为预检结果 task={task.Name} root={scan.SourceRoot}");

        var response = await _api.SubmitPrecheckAsync(task.TaskId, new SubmitPrecheckResultRequest
        {
            CommandId = commandId,
            BusinessUnit = scan.BusinessUnit,
            CandidateKey = scan.CandidateKey,
            SourceRoot = scan.SourceRoot,
            BackupBusinessTime = scan.BackupBusinessTime,
            PrecheckStatus = scan.Status,
            TotalFiles = scan.Files.Count,
            TotalBytes = scan.Files.Sum(f => f.SizeBytes),
            QuickFingerprint = scan.QuickFingerprint,
            ManifestHash = scan.ManifestHash,
            FailureCode = scan.FailureCode,
            FailureMessage = scan.FailureMessage,
            Files = scan.Files
        }, ct);

        if (response.CandidateBackupSetId is not null && scan.Status == "passed")
        {
            // 审计 F-08：清单先落独立文件，再往 state.json 写摘要。
            // 顺序不能反——先写摘要的话，进程在两步之间挂掉会留下一条
            // 「有候选、没清单」的记录，上传时才发现，那时已经建过会话了。
            _candidateFiles.Save(scan.CandidateKey, scan.Files.Select(f => new LocalCandidateFile
            {
                RelativePath = f.RelativePath,
                FullPath = Path.GetFullPath(Path.Combine(scan.SourceRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                SizeBytes = f.SizeBytes,
                Sha256 = f.Sha256 ?? string.Empty
            }).ToList());

            _stateStore.Update(s => s.Candidates[scan.CandidateKey] = new LocalCandidateState
            {
                CandidateBackupSetId = response.CandidateBackupSetId,
                TaskId = task.TaskId,
                CandidateKey = scan.CandidateKey,
                SourceRoot = scan.SourceRoot,
                ManifestHash = scan.ManifestHash ?? string.Empty,
                TotalFiles = scan.Files.Count,
                TotalBytes = scan.Files.Sum(f => f.SizeBytes),
                UpdatedAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>BackupScanResult 的属性是 init-only，改 failureMessage 只能整个重建一份。</summary>
    private static BackupScanResult WithFailureMessage(BackupScanResult scan, string failureMessage) => new()
    {
        TaskId = scan.TaskId,
        CandidateKey = scan.CandidateKey,
        SourceRoot = scan.SourceRoot,
        BusinessUnit = scan.BusinessUnit,
        Status = scan.Status,
        BackupBusinessTime = scan.BackupBusinessTime,
        QuickFingerprint = scan.QuickFingerprint,
        ManifestHash = scan.ManifestHash,
        FailureCode = scan.FailureCode,
        FailureMessage = failureMessage,
        Files = scan.Files,
        IsTestScan = scan.IsTestScan
    };

    private async Task<CommandResult> ExecuteUploadAsync(CommandDto command, CancellationToken ct)
    {
        var candidateId = command.CandidateBackupSetId ?? ReadGuid(command.Payload, "candidateBackupSetId");
        var state = _stateStore.Snapshot();
        var candidate = state.Candidates.Values.FirstOrDefault(c => c.CandidateBackupSetId == candidateId);
        if (candidate is null)
            return CommandResult.FromFailure("CANDIDATE_NOT_FOUND", $"本地没有候选集：{candidateId}");

        // 审计 F-08：文件清单不再随 state.json 一起加载，只在真正要传的时候读一次。
        // 读不出来就明确失败，绝不带着半份清单去建会话——那会传上去一个不完整的备份集。
        var candidateFiles = _candidateFiles.Load(candidate.CandidateKey);
        if (candidateFiles.Count == 0)
        {
            return CommandResult.FromFailure(
                "CANDIDATE_FILES_MISSING",
                $"本地候选文件清单缺失或损坏：{candidate.CandidateKey}，请重新预检");
        }

        var config = LoadVerifiedConfig();
        var task = config?.Tasks.FirstOrDefault(t => t.TaskId == candidate.TaskId);
        var chunkSize = task?.ChunkSizeBytes is >= 4 * 1024 * 1024 and <= 32 * 1024 * 1024
            ? task.ChunkSizeBytes
            : 8 * 1024 * 1024;

        var session = await _api.CreateUploadSessionAsync(new CreateUploadSessionRequest
        {
            CandidateBackupSetId = candidate.CandidateBackupSetId!.Value,
            CommandId = command.Id,
            TotalFiles = candidate.TotalFiles,
            TotalBytes = candidate.TotalBytes,
            ChunkSizeBytes = chunkSize,
            ManifestHash = candidate.ManifestHash,
            IdempotencyKey = candidate.CandidateKey
        }, ct);

        // 限速值优先取指令 payload 里的 bandwidthLimitKbps（服务端三处下发口径之一），
        // 回退到任务配置的 BandwidthLimitKbps；整个上传会话共用一个节流器实例，
        // 这样限速是会话级平均值而不是每块独立算，粒度上没必要做到字节级令牌桶。
        var bandwidthLimitKbps = ReadInt(command.Payload, "bandwidthLimitKbps") ?? task?.BandwidthLimitKbps;
        var throttle = new ChunkThrottle(bandwidthLimitKbps);

        // 审计 A-10：会话一旦建出来，从这里往下的任何失败出口都必须尽力取消它。
        // 原先源文件缺失那条是直接 return，异常那条一路冒到 ExecuteCommandAsync，
        // 两条都不碰会话——服务端于是攒下永久 uploading 会话，攒够
        // max_concurrent_uploads 这台机器就再也传不上去了。
        // 收在一个 try/catch 里而不是在每个出口前各加一句，是因为后者迟早会漏一个。
        // 审计 B-15：ReportProgressAsync 服务端和客户端都实现了，AgentWorker 一次都没调过——
        // 于是「正在传 50GB」这件事在管理端表现为一条什么都不显示的「正在执行」。
        var totalBytes = Math.Max(1L, candidate.TotalBytes);
        var progress = new ProgressReporter(
            (payload, token) => _api.ReportProgressAsync(command.Id, payload, token),
            _logger, command.Id, totalBytes);
        long sentBytes = 0;

        try
        {
            foreach (var remoteFile in session.Files)
            {
                var local = candidateFiles.FirstOrDefault(f => string.Equals(f.RelativePath, remoteFile.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (local is null || !File.Exists(local.FullPath))
                {
                    // 抛而不是 return：让它和分块上传的异常走同一条收尾路径。
                    // 增量备份场景里「预检通过之后源文件被删/改名」很常见，
                    // 这是 A-03 僵尸会话最主要的触发源。
                    throw new UploadAbortedException("LOCAL_FILE_MISSING", $"本地文件不存在：{remoteFile.RelativePath}");
                }

                var missing = await _api.GetMissingChunksAsync(session.UploadSessionId, remoteFile.UploadFileId, ct);
                var indexes = ExpandMissing(missing, local.SizeBytes, session.ChunkSizeBytes);

                // 分块并行发送（原先是严格串行的 foreach）。
                //
                // 串行的代价：一块的完整周期是「读盘 → 算哈希 → HTTP 往返 →
                // 服务端写盘+落库 → 应答回来」，这中间网卡基本是空的。
                // 8MB 在千兆网上只占 0.07 秒，而固定开销远大于此——
                // 换句话说串行时链路利用率只有个位数百分比，这跟磁盘快不快没关系，
                // 是「等一个来回才敢发下一个」这件事本身造成的。
                //
                // 并行 N 路让「等上一块被处理」和「发下一块」重叠起来。服务端本来就支持：
                // 同一文件多块并发写、各块偏移互不重叠（见 UploadStorage.WriteChunkAsync），
                // 分块记录按 (file, index) upsert、计数在数据库侧增量累加，并发落库都是安全的。
                var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(indexes);
                var parallel = Math.Clamp(_options.MaxParallelChunks, 1, 16);
                using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Exception? firstFailure = null;

                async Task RunSenderAsync()
                {
                    while (queue.TryDequeue(out var index))
                    {
                        try
                        {
                            failFast.Token.ThrowIfCancellationRequested();
                            await UploadChunkWithRetryAsync(
                                session.UploadSessionId, remoteFile.UploadFileId, local.FullPath,
                                index, session.ChunkSizeBytes, throttle, failFast.Token);

                            // 只累加本次真正传的块。续传时已在服务端的块不在 indexes 里，
                            // 因此百分比反映的是「这一次要传多少」，不是文件总量——
                            // 对看进度的人来说这才是有意义的那个数。
                            var chunkLength = Math.Min(
                                session.ChunkSizeBytes,
                                local.SizeBytes - (long)index * session.ChunkSizeBytes);
                            var total = Interlocked.Add(ref sentBytes, chunkLength);
                            await progress.ReportAsync(total, local.RelativePath, failFast.Token);
                        }
                        catch (OperationCanceledException) when (failFast.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            // 另一路已经失败、把大家一起叫停了，本路不是自己出错。
                            // 不记录，好让真正的失败原因原样冒出去——否则最终抛出的
                            // 会是一个「已取消」，既掩盖病因，又绕过外层那条
                            // 「非取消才收尾取消会话」的 catch，留下僵尸会话。
                            return;
                        }
                        catch (Exception ex)
                        {
                            // 只留第一个真实失败，并立刻叫停其余几路：
                            // 一块传不上去，剩下的大概率也传不上去，继续传纯属浪费带宽。
                            Interlocked.CompareExchange(ref firstFailure, ex, null);
                            failFast.Cancel();
                            return;
                        }
                    }
                }

                await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => RunSenderAsync()));

                // 外层取消优先：那意味着整个 Agent 正在停机，
                // 不该被包装成「这次上传失败了」。
                ct.ThrowIfCancellationRequested();
                if (firstFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();

                await _api.CompleteUploadFileAsync(session.UploadSessionId, remoteFile.UploadFileId, new CompleteUploadFileRequest
                {
                    SizeBytes = local.SizeBytes,
                    Sha256 = local.Sha256
                }, ct);
            }

            var completed = await _api.CompleteUploadSessionAsync(session.UploadSessionId, new CompleteUploadSessionRequest
            {
                ManifestHash = candidate.ManifestHash,
                TotalFiles = candidate.TotalFiles,
                TotalBytes = candidate.TotalBytes
            }, ct);
            return CommandResult.FromSuccess("UPLOAD_ACCEPTED", $"上传会话已提交校验：{completed.VerificationOperationId}");
        }
        catch (UploadAbortedException ex)
        {
            await TryInterruptSessionAsync(session.UploadSessionId, ex.ResultCode, ex.Message, ct);
            return CommandResult.FromFailure(ex.ResultCode, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryInterruptSessionAsync(session.UploadSessionId, "UPLOAD_ERROR", ex.Message, ct);
            throw;
        }
    }

    /// <summary>
    /// 上传进度节流上报器（审计 B-15）。
    ///
    /// 必须节流：每块上报一次会把 commands 表写爆——50GB / 8MB = 6400 次 UPDATE，
    /// 而进度是给人看的，人不需要每 8MB 看一次。
    /// 两个条件同时满足才发：距上次至少 30 秒，且百分比至少涨了 5 个点。
    ///
    /// 上报失败只记 LogDebug 吞掉：它是装饰，让它把一次正在进行的上传搞失败是本末倒置。
    /// </summary>
    internal sealed class ProgressReporter
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
        private const decimal MinPercentDelta = 5m;

        // 只依赖「怎么把一条进度发出去」，不依赖 AgentApiClient 本身。
        // AgentApiClient 是 sealed、方法非虚、构造还要 HttpClient，挡着这个类没法被测；
        // 而这个类里真正容易写错的是节流判断，跟 HTTP 一点关系都没有。
        private readonly Func<CommandProgressRequest, CancellationToken, Task> _send;
        private readonly ILogger _logger;
        private readonly Guid _commandId;
        private readonly long _totalBytes;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // 初值取「负一个间隔」而不是 TimeSpan.MinValue。
        // TimeSpan.MinValue 的 ticks 是 long.MinValue，Elapsed 减它必然溢出 Int64，
        // 抛出的 OverflowException（消息「TimeSpan overflowed because the duration is too long.」）
        // 会一路冒到 ExecuteUploadAsync 的兜底 catch，把整次上传判成失败——
        // 每块传完都会调 ReportAsync，所以是第一块传完就炸，任何上传都完不成。
        // 语义与下面 -MinPercentDelta 一致：让第一次调用直接放行，
        // 传大文件的人一开始就能看到进度在动，而不是先干等 30 秒。
        private TimeSpan _lastReportedAt = -MinInterval;
        private decimal _lastReportedPercent = -MinPercentDelta;

        public ProgressReporter(
            Func<CommandProgressRequest, CancellationToken, Task> send,
            ILogger logger, Guid commandId, long totalBytes)
        {
            _send = send;
            _logger = logger;
            _commandId = commandId;
            _totalBytes = totalBytes;
        }

        /// <summary>
        /// 节流状态的互斥锁。分块改为并行发送后，多个上传线程会同时调 ReportAsync，
        /// 而「读 _lastReportedAt → 判断 → 写回」是个读-改-写序列，不锁就会有多路
        /// 同时判定放行，把本该 30 秒一次的上报打成一串。
        /// </summary>
        private readonly object _gate = new();

        public async Task ReportAsync(long sentBytes, string currentPath, CancellationToken ct)
        {
            // try 包住整个方法体，而不是只包那次 HTTP 调用。
            // 原先节流判断在 try 之外，它自己抛的异常就绕过了本类
            //「上报只是装饰，绝不能把正在进行的上传搞失败」这条约定，
            // 结果是一个纯展示功能整整挡住了备份主链路。
            try
            {
                decimal percent;
                lock (_gate)
                {
                    percent = Math.Clamp(Math.Round((decimal)sentBytes * 100 / _totalBytes, 2), 0, 100);
                    if (_clock.Elapsed - _lastReportedAt < MinInterval || percent - _lastReportedPercent < MinPercentDelta)
                        return;

                    _lastReportedAt = _clock.Elapsed;
                    _lastReportedPercent = percent;
                }

                await _send(new CommandProgressRequest
                {
                    Percent = percent,
                    Stage = "uploading",
                    Message = currentPath
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "上传进度上报失败 command={CommandId}（不影响上传本身）", _commandId);
            }
        }
    }

    /// <summary>
    /// 上传中止：把「已知原因的失败」变成异常，好让它和意外异常共用同一条收尾路径。
    /// ResultCode 原样回报给服务端，语义与改造前的 CommandResult.FromFailure 一致。
    /// </summary>
    private sealed class UploadAbortedException : Exception
    {
        public UploadAbortedException(string resultCode, string message) : base(message) => ResultCode = resultCode;

        public string ResultCode { get; }
    }

    /// <summary>
    /// 尽力把会话标记为「中断、可续传」，而不是取消它（待办方案 E）。
    ///
    /// 取消的代价：会话置 cancelled、暂存随后被清、幂等键被释放，下一次上传只能从 0 开始。
    /// 于是「断网」能续传（会话还活着），「出错」不能续传——6GB 传到 90% 出一次错就全部重来。
    /// 中断则把会话停在 retry_wait：状态仍可写、暂存仍在，同一候选的下一条上传指令
    /// 凭幂等键拿回这个会话，从缺哪块传哪块接着传。
    ///
    /// 标记失败只记日志——它是收尾动作，让它的异常盖掉真正的失败原因，
    /// 排障时看到的就会是「收尾失败」而不是「文件不存在」。
    /// 服务端仍然必须有 LifecycleExpiryWorker 的超时兜底：客户端可能是被 kill 的，
    /// 根本没机会执行这一步。两者是纵深关系，不是二选一。
    /// </summary>
    private async Task TryInterruptSessionAsync(Guid sessionId, string? code, string? message, CancellationToken ct)
    {
        try
        {
            await _api.InterruptUploadSessionAsync(sessionId,
                new InterruptUploadSessionRequest { ErrorCode = code, ErrorMessage = message }, ct);
            _logger.LogInformation("上传失败，会话 {SessionId} 已标记为可续传（暂存保留）", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上传失败后标记会话可续传未成功 {SessionId}（服务端超时巡检会兜底）", sessionId);
        }
    }

    private async Task UploadChunkWithRetryAsync(Guid sessionId, Guid fileId, string path, int chunkIndex, int chunkSize, ChunkThrottle throttle, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var offset = (long)chunkIndex * chunkSize;
        var length = (int)Math.Min(chunkSize, info.Length - offset);
        if (length <= 0)
            return;

        // 分块默认 8MB，逐块裸分配全部落 LOH；改用 ArrayPool 复用。
        // 风险点：Rent 返回的数组可能比请求的大，下游一律按 length 切片，
        // 绝不能把整个 buffer 传出去，否则会把池里的垃圾字节一起传上去。
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct);
                if (count == 0)
                    throw new IOException($"读取文件时提前结束：{path}");
                read += count;
            }

            var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length))).ToLowerInvariant();
            Exception? last = null;
            for (var attempt = 1; attempt <= Math.Max(1, _options.MaxUploadRetries); attempt++)
            {
                try
                {
                    await _api.UploadChunkAsync(sessionId, fileId, chunkIndex, offset, buffer.AsMemory(0, length), hash, ct);
                    // 节流放在上传成功之后而不是之前：失败重试的那几次不重复计费。
                    await throttle.AfterChunkAsync(length, ct);
                    return;
                }
                catch (Exception ex) when (ex is AgentApiException or HttpRequestException)
                {
                    last = ex;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 10)), ct);
                }
            }

            throw last ?? new IOException($"分块上传失败：{path}#{chunkIndex}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 分块粒度的节流器：块级精度对 KB/s 量级的限速已经足够，不需要字节级令牌桶。
    /// 会话级共用一个实例，限速是整个上传会话的平均值，而不是每块独立算。
    /// 用 Task.Delay 让出线程，不阻塞任何工作线程，也不会影响心跳。
    /// </summary>
    internal sealed class ChunkThrottle
    {
        /// <summary>
        /// 累计等待时间的上界（一天）。见 ComputeDelay 里的说明。
        /// </summary>
        private const double MaxThrottleSeconds = 86400d;

        private readonly long _bytesPerSecond;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _sentBytes;

        public ChunkThrottle(int? kbps) => _bytesPerSecond = ToBytesPerSecond(kbps);

        /// <summary>
        /// kbps 换算成字节/秒。必须先转 long 再乘：int × int 在 kbps 超过 2^21 时溢出，
        /// 绕回来可能是个很小的正数——「限速 4 GB/s」实际变成限速 1 KB/s，
        /// 而这种错误在界面上完全看不出来，只表现为传得莫名其妙地慢。
        /// </summary>
        internal static long ToBytesPerSecond(int? kbps) => kbps is > 0 ? (long)kbps.Value * 1024 : 0;

        /// <summary>
        /// 这一块传完之后该等多久。
        ///
        /// 拆成静态纯函数是为了能测：原来的写法要真的跑起 Stopwatch 和 Task.Delay
        /// 才验得了，等于验不了，而限速算错的表现是「传得慢」——最不容易被发现的那种故障。
        ///
        /// 秒数夹一个上界再进 TimeSpan：FromSeconds 超出表示范围会抛 OverflowException。
        /// 同一个文件里的 ProgressReporter 刚因为一次 TimeSpan 溢出把所有上传打挂过，
        /// 不给它第二次机会。真跑到这个上界说明限速值本身就配错了。
        /// </summary>
        internal static TimeSpan ComputeDelay(long bytesPerSecond, long sentBytes, TimeSpan elapsed)
        {
            if (bytesPerSecond <= 0)
                return TimeSpan.Zero;

            var seconds = Math.Min((double)sentBytes / bytesPerSecond, MaxThrottleSeconds);
            var delay = TimeSpan.FromSeconds(seconds) - elapsed;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        /// <summary>
        /// 累计字节数的互斥锁。分块并行发送后多路会同时到达这里，
        /// `_sentBytes += bytes` 不是原子操作，不锁会漏计——而漏计的表现是限速偏松，
        /// 也就是「限了速但没限住」，恰好是最不容易被发现的那种故障。
        /// </summary>
        private readonly object _gate = new();

        public async Task AfterChunkAsync(int bytes, CancellationToken ct)
        {
            if (_bytesPerSecond <= 0)
                return;

            TimeSpan delay;
            lock (_gate)
            {
                _sentBytes += bytes;
                delay = ComputeDelay(_bytesPerSecond, _sentBytes, _clock.Elapsed);
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
        }
    }

    private async Task<CommandResult> StageUpgradeAsync(CommandDto command, CancellationToken ct)
    {
        var url = ReadString(command.Payload, "packageUrl")
            ?? ReadString(command.Payload, "packagePath");
        var expectedHash = ReadString(command.Payload, "sha256")
            ?? ReadString(command.Payload, "packageSha256");
        var version = ReadString(command.Payload, "version")
            ?? ReadString(command.Payload, "targetVersion")
            ?? DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        if (string.IsNullOrWhiteSpace(url))
            return CommandResult.FromFailure("UPGRADE_PACKAGE_URL_REQUIRED", "升级指令缺少 packageUrl");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var packageUri)
            || (!string.Equals(packageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(packageUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return CommandResult.FromFailure("UPGRADE_PACKAGE_URL_INVALID", "升级包必须使用 HTTP 或 HTTPS 地址");
        }

        // 纵深防御（审计 C-01）：签名覆盖 payload 之后这一条在理论上是冗余的，
        // 但升级是全系统唯一一条「服务端说什么就执行什么代码」的路径，值得两道锁。
        // 只比 host:port，不比 scheme——Secure 形态下 nginx 终结 TLS 后回环到 Kestrel，
        // 两端的 scheme 本来就可能不同。
        if (!IsSameServerAuthority(packageUri))
        {
            return CommandResult.FromFailure(
                "UPGRADE_PACKAGE_URL_FORBIDDEN",
                $"升级包地址不在本 Agent 的服务端上：{packageUri.Host}:{packageUri.Port}");
        }

        if (!Regex.IsMatch(expectedHash ?? string.Empty, "^[0-9a-fA-F]{64}$"))
            return CommandResult.FromFailure("UPGRADE_HASH_REQUIRED", "升级包必须提供 64 位 SHA-256");

        var bytes = await _api.DownloadBytesAsync(packageUri.ToString(), ct);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(expectedHash) && !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            return CommandResult.FromFailure("UPGRADE_HASH_MISMATCH", "升级包 SHA-256 校验失败");

        var updateRoot = Path.Combine(_options.ExpandedDataDirectory, "updates", version);
        Directory.CreateDirectory(updateRoot);
        var packagePath = Path.Combine(updateRoot, "BackupMonitor.Agent.zip");
        await File.WriteAllBytesAsync(packagePath, bytes, ct);
        ZipFile.ExtractToDirectory(packagePath, updateRoot, overwriteFiles: true);
        await File.WriteAllTextAsync(Path.Combine(_options.ExpandedDataDirectory, "pending-update.json"),
            JsonSerializer.Serialize(new { version, packagePath, sha256 = actualHash, stagedAt = DateTime.UtcNow }), ct);
        return CommandResult.FromSuccess("UPGRADE_STAGED", $"升级包已安全解压到 {updateRoot}，将在受控重启时切换");
    }

    private bool IsSameServerAuthority(Uri packageUri) => IsSameServerAuthority(packageUri, _options.ServerUrl);

    /// <summary>
    /// 升级包地址是否指向本 Agent 自己的服务端（只比 host:port）。
    /// serverUrl 解析不出来时一律拒绝——配置坏掉的时候更不该放行任意下载。
    ///
    /// 拆成静态方法是为了能单测：整个 AgentWorker 的依赖太重，
    /// 为一条 host 比对去把它组装出来不划算。
    /// </summary>
    public static bool IsSameServerAuthority(Uri packageUri, string? serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
            return false;

        return string.Equals(packageUri.Host, serverUri.Host, StringComparison.OrdinalIgnoreCase)
               && packageUri.Port == serverUri.Port;
    }

    /// <summary>
    /// 服务端回的「还缺哪些块」展开成块号序列。internal 是为了能测：
    /// 展开错了的后果是漏传几个块，而漏传的块在会话完成校验之前不会有任何征兆。
    /// </summary>
    internal static IEnumerable<int> ExpandMissing(MissingChunksResponse missing, long size, int chunkSize)
    {
        if (missing.Missing is { Count: > 0 })
            return missing.Missing;
        if (missing.MissingRanges is { Count: > 0 })
            return missing.MissingRanges.SelectMany(r => Enumerable.Range(r.Start, r.End - r.Start + 1));
        if (missing.Missing is { Count: 0 })
            return [];
        return Enumerable.Range(0, (int)Math.Ceiling(size / (double)chunkSize));
    }

    private static Guid? ReadGuid(string? payload, string name)
    {
        var value = ReadString(payload, name);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private static int? ReadInt(string? payload, string name)
    {
        var value = ReadString(payload, name);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? ReadString(string? payload, string name)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty(name, out var property) ? property.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按任务自带的 cron 在本地触发扫描。
    ///
    /// 调度做在 Agent 侧而不是服务端：cron 已经随签名配置下发了，Agent 自己算下一次触发
    /// 不需要服务端调度器和分布式锁；服务端短暂离线也不会让当天的扫描整个丢掉。
    /// 触发的动作与手动预检完全一致（扫描 + 上报预检结果），只是不带指令 ID。
    ///
    /// 排期状态持久化在 state.json 里，Agent 重启不会丢；反过来，重启也不会
    /// 立刻重跑一遍——只有确实越过了触发时刻才会执行。
    /// </summary>
    private async Task RunScheduledScansAsync(AgentConfigResponse? config, CancellationToken ct)
    {
        if (config is null)
            return;

        // 「暂停」只改 TaskMode、不动 Enabled，所以这里必须显式排除 paused——
        // 否则点了暂停之后定时扫描照跑，暂停这个按钮就等于失效了。
        // 手动预检不受影响：那是操作员的明确指令。
        var scheduled = config.Tasks
            .Where(t => t.Enabled
                        && !string.Equals(t.TaskMode, "paused", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(t.ScanSchedule))
            .ToList();

        // 任务被删除、停用或取消了定时之后，排期记录要跟着清掉，否则状态文件只增不减。
        var live = new HashSet<string>(scheduled.Select(t => t.TaskId.ToString()), StringComparer.OrdinalIgnoreCase);
        _stateStore.Update(state =>
        {
            foreach (var stale in state.ScheduledScans.Keys.Where(key => !live.Contains(key)).ToList())
                state.ScheduledScans.Remove(stale);
        });

        foreach (var task in scheduled)
        {
            ct.ThrowIfCancellationRequested();

            if (!CronExpression.TryParse(task.ScanSchedule, out var cron, out var parseError) || cron is null)
            {
                // 服务端保存时已经校验过，走到这里说明是旧数据或被绕过；跳过该任务而不是整轮中断。
                _logger.LogWarning(
                    "任务 {Task} 的扫描计划无法解析，本次跳过：{Error}（cron={Cron}）",
                    task.Name, parseError, task.ScanSchedule);
                continue;
            }

            var timeZone = ResolveScheduleTimeZone(task.ScheduleTimezone);
            var key = task.TaskId.ToString();
            var now = DateTime.UtcNow;
            var entry = _stateStore.Snapshot().ScheduledScans.GetValueOrDefault(key);

            // 首次见到这个任务，或者 cron / 时区被改过：从此刻起重新排期，不补跑历史。
            if (entry is null
                || !string.Equals(entry.Cron, task.ScanSchedule, StringComparison.Ordinal)
                || !string.Equals(entry.TimeZone, timeZone.Id, StringComparison.Ordinal))
            {
                var first = cron.GetNextOccurrence(now, timeZone);
                var firstDue = SaveScheduleEntry(key, task.ScanSchedule!, timeZone.Id, first, lastRunAtUtc: entry?.LastRunAtUtc, task.RandomDelayMinutes);
                _logger.LogInformation(
                    "任务 {Task} 的扫描计划已排期 cron={Cron} tz={TimeZone} next={Next:O}",
                    task.Name, task.ScanSchedule, timeZone.Id, firstDue);
                continue;
            }

            if (entry.NextDueAtUtc > now)
                continue;

            // 先写回下一次排期再执行：扫描可能耗时很久，中途崩溃不该在重启后重复触发同一次计划。
            var following = cron.GetNextOccurrence(now, timeZone);
            var followingDue = SaveScheduleEntry(key, task.ScanSchedule!, timeZone.Id, following, lastRunAtUtc: now, task.RandomDelayMinutes);

            _logger.LogInformation(
                "按扫描计划触发预检 task={Task} cron={Cron} next={Next:O}", task.Name, task.ScanSchedule, followingDue);
            try
            {
                var submitted = await SubmitScanResultsAsync(task, commandId: null, ct);
                _logger.LogInformation("计划扫描完成 task={Task} 已提交 {Count} 个预检结果", task.Name, submitted);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 一个任务失败不影响其余任务，也不该把整个工作循环打回重试。
                _logger.LogError(ex, "计划扫描失败 task={Task}", task.Name);
                _stateStore.Update(state => state.LastError = ex.Message);
            }
        }
    }

    /// <summary>
    /// 写回排期。nextDueAtUtc 为 null 表示这个表达式描述的时刻永不出现
    /// （例如 2 月 30 日），记成 MaxValue，免得每一轮都重算一次并刷日志。
    ///
    /// randomDelayMinutes 大于 0 时叠加 [0, randomDelayMinutes] 分钟的随机偏移，
    /// 用来错开多台客户端在同一 cron 时刻同时扫描/上传。返回值是叠加偏移后
    /// 实际写入状态的时刻，调用方的日志要用这个值，而不是叠加前的 cron 时刻，
    /// 否则日志里的 next= 看不出随机偏移生效了。
    /// </summary>
    private DateTime SaveScheduleEntry(string key, string cron, string timeZoneId, DateTime? nextDueAtUtc, DateTime? lastRunAtUtc, int randomDelayMinutes = 0)
    {
        if (nextDueAtUtc is null)
            _logger.LogWarning("扫描计划 {Cron} 在未来四年内没有触发时刻，已停用该任务的定时扫描", cron);

        var due = nextDueAtUtc is null
            ? DateTime.MaxValue
            : randomDelayMinutes > 0
                ? nextDueAtUtc.Value.Add(TimeSpan.FromMinutes(Random.Shared.Next(0, randomDelayMinutes + 1)))
                : nextDueAtUtc.Value;

        _stateStore.Update(state => state.ScheduledScans[key] = new ScheduledScanState
        {
            Cron = cron,
            TimeZone = timeZoneId,
            NextDueAtUtc = due,
            LastRunAtUtc = lastRunAtUtc
        });

        return due;
    }

    /// <summary>
    /// 解析任务时区。服务端存的是 IANA ID（默认 Asia/Shanghai），
    /// .NET 在 Windows 上也能识别；识别不了就退回本机时区，不能因此不扫描。
    /// </summary>
    private TimeZoneInfo ResolveScheduleTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Local;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogWarning("无法识别时区 {TimeZone}，扫描计划按本机时区执行", timeZoneId);
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// 从磁盘读一次配置并验签，结果写进 <see cref="_config"/> 缓存（审计 B-02）。
    ///
    /// 验签是 RSA 运算，三条循环各自反复调没有意义——配置只在 SyncConfigAsync
    /// 成功之后才会变，那一刻更新缓存就够了。
    /// </summary>
    private AgentConfigResponse? RefreshConfigCache()
    {
        var config = _configStore.Load();
        var clientId = _stateStore.Snapshot().ClientId;
        _config = config is not null
                  && clientId is not null
                  && _signatures.VerifyConfig(config, clientId.Value)
            ? config
            : null;
        return _config;
    }

    /// <summary>缓存里的配置；为空时（首次启动、刚注册完）现场读一次。</summary>
    private AgentConfigResponse? LoadVerifiedConfig() => _config ?? RefreshConfigCache();

    private string? ReadRegistrationToken()
    {
        if (!string.IsNullOrWhiteSpace(_options.RegistrationToken))
            return _options.RegistrationToken.Trim();

        try
        {
            return File.Exists(_options.ExpandedRegistrationTokenPath)
                ? File.ReadAllText(_options.ExpandedRegistrationTokenPath).Trim()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法读取注册令牌文件 {Path}", _options.ExpandedRegistrationTokenPath);
            return null;
        }
    }

    private void ClearRegistrationToken()
    {
        try
        {
            if (File.Exists(_options.ExpandedRegistrationTokenPath))
                File.Delete(_options.ExpandedRegistrationTokenPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注册成功但无法删除注册令牌文件 {Path}", _options.ExpandedRegistrationTokenPath);
        }

        // 兼容旧版本把令牌写入 appsettings.json 的安装包，成功注册后清空旧字段。
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(settingsPath))
                return;

            var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            if (root?["Agent"] is not JsonObject agent
                || !agent.ContainsKey("RegistrationToken")
                || string.IsNullOrEmpty(agent["RegistrationToken"]?.GetValue<string>()))
                return;

            agent["RegistrationToken"] = string.Empty;
            File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注册成功但无法清空旧版 appsettings.json 中的 RegistrationToken");
        }
    }

    private sealed record CommandResult(bool Success, string Code, string Message, string? ResultJson = null)
    {
        public static CommandResult FromSuccess(string code, string message) => new(true, code, message);
        public static CommandResult FromFailure(string code, string message) => new(false, code, message);
    }
}
