using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Scheduling;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Agent;

public sealed class AgentWorker : BackgroundService
{
    private static readonly string[] SupportedCommands =
    [
        "precheck_task", "precheck_all", "upload_candidate", "upload_latest",
        "rescan", "rehash", "sync_config", "refresh_metrics", "upgrade_agent"
    ];

    private readonly AgentOptions _options;
    private readonly AgentStateStore _stateStore;
    private readonly AgentTrayNotificationStore _trayNotifications;
    private readonly AgentConfigStore _configStore;
    private readonly AgentApiClient _api;
    private readonly AgentSignatureVerifier _signatures;
    private readonly SystemProbe _probe;
    private readonly BackupScanner _scanner;
    private readonly ILogger<AgentWorker> _logger;
    private readonly HashSet<Guid> _activeCommands = [];

    public AgentWorker(
        IOptions<AgentOptions> options,
        AgentStateStore stateStore,
        AgentTrayNotificationStore trayNotifications,
        AgentConfigStore configStore,
        AgentApiClient api,
        AgentSignatureVerifier signatures,
        SystemProbe probe,
        BackupScanner scanner,
        ILogger<AgentWorker> logger)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _trayNotifications = trayNotifications;
        _configStore = configStore;
        _api = api;
        _signatures = signatures;
        _probe = probe;
        _scanner = scanner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackupMonitor Agent {Version} 启动，服务器={Server}", _options.AgentVersion, _options.ServerUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_stateStore.Snapshot().ClientId is null)
                {
                    if (!await EnsureRegisteredAsync(stoppingToken))
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                await RenewCertificateIfNeededAsync(stoppingToken);

                var config = LoadVerifiedConfig();
                var state = _stateStore.Snapshot();
                var heartbeat = await _api.HeartbeatAsync(
                    _probe.BuildHeartbeat(state.ConfigVersion, config, _activeCommands), stoppingToken);
                ProcessNotifications(heartbeat.Notifications);

                if (heartbeat.CertificateRenewalRequired)
                    await RenewCertificateIfNeededAsync(stoppingToken, force: true);

                if (heartbeat.RequiredConfigVersion > state.ConfigVersion)
                    config = await SyncConfigAsync(stoppingToken) ?? config;

                if (heartbeat.CommandsAvailable || _activeCommands.Count == 0)
                    await ClaimAndExecuteCommandsAsync(stoppingToken);

                await RunScheduledScansAsync(config, stoppingToken);

                var heartbeatDelay = heartbeat.HeartbeatIntervalSeconds > 0
                    ? heartbeat.HeartbeatIntervalSeconds
                    : _options.HeartbeatIntervalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Math.Min(heartbeatDelay, _options.CommandPollIntervalSeconds), 2, 60)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentApiException ex)
            {
                _logger.LogWarning("Agent API 调用失败 status={Status} code={Code}: {Message}", ex.StatusCode, ex.ErrorCode, ex.Message);
                _stateStore.Update(s => s.LastError = ex.Message);

                // mTLS 形态下 401 意味着服务端没有收到（或不认）本次连接出示的客户端证书。
                // 本地明明装着证书却被拒，最可能的原因是连接的 TLS 身份与本地证书脱节——
                // 证书是握手时协商的，池中的旧连接不会因为本地换了证书而改变身份。
                // 重新握手即可自愈；代价是每 10 秒一次 TLS 握手，可以忽略。
                if (ex.StatusCode == 401 && _stateStore.Snapshot().CertificateThumbprint is not null)
                    _api.RecycleConnections();

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 工作循环异常");
                _stateStore.Update(s => s.LastError = ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("BackupMonitor Agent 停止");
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

        var seen = _stateStore.Snapshot().SeenNotificationIds;
        foreach (var notification in notifications)
        {
            if (notification.Id == Guid.Empty || seen.Contains(notification.Id))
                continue;

            if (!_trayNotifications.TryEnqueue(notification))
                continue;

            _stateStore.Update(state =>
            {
                if (!state.SeenNotificationIds.Contains(notification.Id))
                    state.SeenNotificationIds.Add(notification.Id);

                if (state.SeenNotificationIds.Count > 500)
                    state.SeenNotificationIds = state.SeenNotificationIds.TakeLast(500).ToList();
            });
            seen.Add(notification.Id);
        }
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

    private async Task<AgentConfigResponse?> SyncConfigAsync(CancellationToken ct)
    {
        var version = _stateStore.Snapshot().ConfigVersion;
        var config = await _api.GetConfigAsync(version, ct);
        if (config is null)
            return LoadVerifiedConfig();

        var clientId = _stateStore.Snapshot().ClientId;
        if (clientId is null || !_signatures.VerifyConfig(config, clientId.Value))
            throw new AgentApiException(498, "服务端配置签名校验失败", "CONFIG_SIGNATURE_INVALID");

        _configStore.Save(config);
        _stateStore.Update(s =>
        {
            s.ConfigVersion = config.Version;
            s.LastError = null;
        });
        _logger.LogInformation("Agent 配置已同步 version={Version} tasks={Tasks}", config.Version, config.Tasks.Count);
        return config;
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
            _activeCommands.Add(command.Id);
            try
            {
                await ExecuteCommandAsync(command, ct);
            }
            finally
            {
                _activeCommands.Remove(command.Id);
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
                "precheck_task" => await ExecutePrecheckAsync(command.TaskId, command.Id, ct),
                "precheck_all" => await ExecutePrecheckAllAsync(command.Id, ct),
                "upload_candidate" or "upload_latest" => await ExecuteUploadAsync(command, ct),
                "rescan" or "rehash" => await ExecutePrecheckAsync(command.TaskId, command.Id, ct),
                "sync_config" => await SyncConfigAsync(ct) is not null
                    ? CommandResult.FromSuccess("CONFIG_SYNCED", "配置已同步")
                    : CommandResult.FromFailure("CONFIG_NOT_CHANGED", "配置未变化"),
                "refresh_metrics" => CommandResult.FromSuccess("OK", "指标将在下一次心跳上报"),
                "upgrade_agent" => await StageUpgradeAsync(command, ct),
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
        foreach (var scan in scans)
        {
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
                _stateStore.Update(s => s.Candidates[scan.CandidateKey] = new LocalCandidateState
                {
                    CandidateBackupSetId = response.CandidateBackupSetId,
                    TaskId = task.TaskId,
                    CandidateKey = scan.CandidateKey,
                    SourceRoot = scan.SourceRoot,
                    ManifestHash = scan.ManifestHash ?? string.Empty,
                    TotalFiles = scan.Files.Count,
                    TotalBytes = scan.Files.Sum(f => f.SizeBytes),
                    Files = scan.Files.Select(f => new LocalCandidateFile
                    {
                        RelativePath = f.RelativePath,
                        FullPath = Path.GetFullPath(Path.Combine(scan.SourceRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                        SizeBytes = f.SizeBytes,
                        Sha256 = f.Sha256 ?? string.Empty
                    }).ToList(),
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        return scans.Count;
    }

    private async Task<CommandResult> ExecuteUploadAsync(CommandDto command, CancellationToken ct)
    {
        var candidateId = command.CandidateBackupSetId ?? ReadGuid(command.Payload, "candidateBackupSetId");
        var state = _stateStore.Snapshot();
        var candidate = state.Candidates.Values.FirstOrDefault(c => c.CandidateBackupSetId == candidateId);
        if (candidate is null)
            return CommandResult.FromFailure("CANDIDATE_NOT_FOUND", $"本地没有候选集：{candidateId}");

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

        foreach (var remoteFile in session.Files)
        {
            var local = candidate.Files.FirstOrDefault(f => string.Equals(f.RelativePath, remoteFile.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (local is null || !File.Exists(local.FullPath))
                return CommandResult.FromFailure("LOCAL_FILE_MISSING", $"本地文件不存在：{remoteFile.RelativePath}");

            var missing = await _api.GetMissingChunksAsync(session.UploadSessionId, remoteFile.UploadFileId, ct);
            var indexes = ExpandMissing(missing, local.SizeBytes, session.ChunkSizeBytes);
            foreach (var index in indexes)
            {
                await UploadChunkWithRetryAsync(session.UploadSessionId, remoteFile.UploadFileId, local.FullPath, index, session.ChunkSizeBytes, ct);
            }

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

    private async Task UploadChunkWithRetryAsync(Guid sessionId, Guid fileId, string path, int chunkIndex, int chunkSize, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var offset = (long)chunkIndex * chunkSize;
        var length = (int)Math.Min(chunkSize, info.Length - offset);
        if (length <= 0)
            return;

        var bytes = new byte[length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read, length - read), ct);
            if (count == 0)
                throw new IOException($"读取文件时提前结束：{path}");
            read += count;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxUploadRetries); attempt++)
        {
            try
            {
                await _api.UploadChunkAsync(sessionId, fileId, chunkIndex, offset, bytes, hash, ct);
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

    private static IEnumerable<int> ExpandMissing(MissingChunksResponse missing, long size, int chunkSize)
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
                SaveScheduleEntry(key, task.ScanSchedule!, timeZone.Id, first, lastRunAtUtc: entry?.LastRunAtUtc);
                _logger.LogInformation(
                    "任务 {Task} 的扫描计划已排期 cron={Cron} tz={TimeZone} next={Next:O}",
                    task.Name, task.ScanSchedule, timeZone.Id, first);
                continue;
            }

            if (entry.NextDueAtUtc > now)
                continue;

            // 先写回下一次排期再执行：扫描可能耗时很久，中途崩溃不该在重启后重复触发同一次计划。
            var following = cron.GetNextOccurrence(now, timeZone);
            SaveScheduleEntry(key, task.ScanSchedule!, timeZone.Id, following, lastRunAtUtc: now);

            _logger.LogInformation(
                "按扫描计划触发预检 task={Task} cron={Cron} next={Next:O}", task.Name, task.ScanSchedule, following);
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
    /// </summary>
    private void SaveScheduleEntry(string key, string cron, string timeZoneId, DateTime? nextDueAtUtc, DateTime? lastRunAtUtc)
    {
        if (nextDueAtUtc is null)
            _logger.LogWarning("扫描计划 {Cron} 在未来四年内没有触发时刻，已停用该任务的定时扫描", cron);

        _stateStore.Update(state => state.ScheduledScans[key] = new ScheduledScanState
        {
            Cron = cron,
            TimeZone = timeZoneId,
            NextDueAtUtc = nextDueAtUtc ?? DateTime.MaxValue,
            LastRunAtUtc = lastRunAtUtc
        });
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

    private AgentConfigResponse? LoadVerifiedConfig()
    {
        var config = _configStore.Load();
        var clientId = _stateStore.Snapshot().ClientId;
        return config is not null
               && clientId is not null
               && _signatures.VerifyConfig(config, clientId.Value)
            ? config
            : null;
    }

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
