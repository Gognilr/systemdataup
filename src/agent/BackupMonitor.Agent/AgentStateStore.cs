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
    public Dictionary<string, LocalCandidateState> Candidates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Guid> SeenNotificationIds { get; set; } = [];
    public List<string> SeenCommandNonces { get; set; } = [];

    /// <summary>按任务 ID 记录扫描计划的排期状态；键为 taskId 的字符串形式。</summary>
    public Dictionary<string, ScheduledScanState> ScheduledScans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
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

public sealed class LocalCandidateState
{
    public Guid? CandidateBackupSetId { get; set; }
    public Guid TaskId { get; set; }
    public string CandidateKey { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public List<LocalCandidateFile> Files { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
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
                return new X509Certificate2(pfx, string.Empty, X509KeyStorageFlags.EphemeralKeySet);
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
        try
        {
            if (File.Exists(_statePath))
                return JsonSerializer.Deserialize<AgentState>(File.ReadAllText(_statePath), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 Agent 状态文件失败，将创建新状态");
        }

        return new();
    }

    private void SaveUnsafe()
    {
        if (_state.SeenNotificationIds.Count > MaxSeenItems)
            _state.SeenNotificationIds = _state.SeenNotificationIds.TakeLast(MaxSeenItems).ToList();
        if (_state.SeenCommandNonces.Count > MaxSeenItems)
            _state.SeenCommandNonces = _state.SeenCommandNonces.TakeLast(MaxSeenItems).ToList();

        var tempPath = _statePath + ".tmp";
        using (File.Create(tempPath))
        {
        }
        SecureFileSystem.ApplyFileAcl(tempPath, _options.EnforceAcl);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_state, JsonOptions));
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
