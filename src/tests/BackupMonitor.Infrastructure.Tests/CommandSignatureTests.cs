using System.Security.Cryptography;
using System.Text;
using BackupMonitor.Agent;
using BackupMonitor.Core.Entities.Backup;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 指令签名 v2 单元测试（审计 C-01）。
///
/// v1 只签了 id·nonce·type·clientId·expiresAt，而真正决定这条指令做什么的
/// taskId / candidateBackupSetId / payload 全在签名之外——能改写响应体的人
/// 可以把 upgrade_agent 的 packageUrl 和 sha256 一起换掉而验签照样返回 true。
/// 这组用例盯住的就是「改了这三个字段签名必须失效」。
/// 纯内存 RSA 密钥对，不依赖数据库。
/// </summary>
public sealed class CommandSignatureTests
{
    private static readonly Guid CommandId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TaskId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid CandidateId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    /// <summary>模拟从 jsonb 读回来的规范化文本：签名与验签两端必须逐字节用同一份</summary>
    private const string Payload = "{\"packageUrl\": \"https://server:8443/downloads/agent.zip\", \"sha256\": \"ab\"}";

    [Fact]
    public void 改动_payload_任意一个字节都会让验签失败()
    {
        var (signer, verifier) = BuildPair();
        var command = NewCommand(Payload);
        var dto = ToDto(command, signer.SignCommand(command));

        Assert.True(verifier.VerifyCommand(dto, ClientId));

        // 攻击者把升级包地址换成自己的主机——v1 时代这一步验签毫无察觉
        dto.Payload = Payload.Replace("https://server:8443", "https://evil.example.com");
        Assert.False(verifier.VerifyCommand(dto, ClientId));

        // 连改一个字节也不行
        dto.Payload = Payload.Replace("\"ab\"", "\"ac\"");
        Assert.False(verifier.VerifyCommand(dto, ClientId));
    }

    [Fact]
    public void 改动_taskId_或候选集_id_都会让验签失败()
    {
        var (signer, verifier) = BuildPair();
        var command = NewCommand(Payload);
        var dto = ToDto(command, signer.SignCommand(command));

        dto.TaskId = Guid.NewGuid();
        Assert.False(verifier.VerifyCommand(dto, ClientId));

        dto.TaskId = TaskId;
        dto.CandidateBackupSetId = Guid.NewGuid();
        Assert.False(verifier.VerifyCommand(dto, ClientId));

        // 复原后仍然通过，说明上面两次失败确实是这两个字段引起的
        dto.CandidateBackupSetId = CandidateId;
        Assert.True(verifier.VerifyCommand(dto, ClientId));
    }

    [Fact]
    public void 用_v1_算法签的指令仍然验签通过()
    {
        // 发布顺序要求 Agent 先于服务端升级，因此新 Agent 必须能验
        // 尚未升级的服务端签出来的 v1 指令，否则灰度期间全网指令拒收。
        var (rsa, verifier) = BuildVerifier();
        using var _ = rsa;

        var command = NewCommand(Payload);
        var v1Payload = AgentSignatureCanonicalizer.CommandPayloadV1(
            command.Id,
            command.Nonce,
            EnumMapping.ToSnakeCase(command.CommandType),
            command.ClientId,
            command.ExpiresAt);
        var signature = "rsa-sha256:" + ToBase64Url(
            rsa.SignData(v1Payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        Assert.True(verifier.VerifyCommand(ToDto(command, signature), ClientId));
    }

    [Fact]
    public void payload_为空与为空串产生不同的签名原文()
    {
        // JoinFields 对 null 写 "~"，对空串写空的 Base64——两者必须可区分，
        // 否则「把 payload 整个删掉」和「把 payload 清空」在签名看来是同一件事。
        var nullPayload = Canonical(null);
        var emptyPayload = Canonical(string.Empty);

        Assert.NotEqual(nullPayload, emptyPayload);
        Assert.EndsWith("|~|~|~", Encoding.UTF8.GetString(Canonical(null, noIds: true)));
    }

    [Fact]
    public void 升级包地址指向外部主机时被拒绝()
    {
        const string serverUrl = "https://backup.internal:8443";

        Assert.True(AgentWorker.IsSameServerAuthority(new Uri("https://backup.internal:8443/downloads/a.zip"), serverUrl));
        // Secure 形态下 nginx 终结 TLS 后回环到 Kestrel，scheme 本来就可能不同，只比 host:port
        Assert.True(AgentWorker.IsSameServerAuthority(new Uri("http://BACKUP.INTERNAL:8443/downloads/a.zip"), serverUrl));

        Assert.False(AgentWorker.IsSameServerAuthority(new Uri("https://evil.example.com/a.zip"), serverUrl));
        Assert.False(AgentWorker.IsSameServerAuthority(new Uri("https://backup.internal:9000/a.zip"), serverUrl));
        // 服务端地址配坏时一律拒绝，不放行任意下载
        Assert.False(AgentWorker.IsSameServerAuthority(new Uri("https://backup.internal:8443/a.zip"), "不是一个地址"));
    }

    // ---------- 基础设施 ----------

    private static byte[] Canonical(string? payload, bool noIds = false) =>
        AgentSignatureCanonicalizer.CommandPayload(
            CommandId,
            "nonce-1",
            "upgrade_agent",
            ClientId,
            new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc),
            noIds ? null : TaskId,
            noIds ? null : CandidateId,
            payload);

    private static (CommandSigner Signer, AgentSignatureVerifier Verifier) BuildPair()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        return (new CommandSigner(Configuration("Security:CommandSigningPrivateKey", privateKey)),
                NewVerifier(publicKey));
    }

    private static (RSA Rsa, AgentSignatureVerifier Verifier) BuildVerifier()
    {
        var rsa = RSA.Create(2048);
        return (rsa, NewVerifier(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())));
    }

    private static AgentSignatureVerifier NewVerifier(string publicKey) =>
        new(Options.Create(new AgentOptions { ServerSigningPublicKey = publicKey }),
            NullLogger<AgentSignatureVerifier>.Instance);

    private static IConfiguration Configuration(string key, string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static Command NewCommand(string? payload) => new()
    {
        Id = CommandId,
        ClientId = ClientId,
        TaskId = TaskId,
        CandidateBackupSetId = CandidateId,
        CommandType = CommandType.UpgradeAgent,
        Nonce = "nonce-1",
        Payload = payload,
        ExpiresAt = DateTime.UtcNow.AddMinutes(30)
    };

    private static CommandDto ToDto(Command command, string signature) => new()
    {
        Id = command.Id,
        Type = EnumMapping.ToSnakeCase(command.CommandType),
        TaskId = command.TaskId,
        CandidateBackupSetId = command.CandidateBackupSetId,
        Payload = command.Payload,
        ExpiresAt = command.ExpiresAt,
        Nonce = command.Nonce,
        Signature = signature
    };

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
