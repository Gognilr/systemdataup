using System.Security.Cryptography;
using BackupMonitor.Api.Bootstrap;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Exceptions;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

[Collection("postgres")]
public sealed class AgentRegistrationTurnkeyTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public AgentRegistrationTurnkeyTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LanSimple_WithoutToken_ApprovesAndPersistsCertificateAndAudit()
    {
        var root = CreateTempRoot();
        try
        {
            var (configuration, bootstrap) = CreateLanConfiguration(root);
            await using var db = CreateDbContext();
            var context = new TestCurrentContext("192.168.20.15");
            var service = CreateService(db, configuration, context);
            var machineId = $"lan-{Guid.NewGuid():N}";

            var response = await service.SubmitAsync(new SubmitRegistrationRequest
            {
                MachineId = machineId,
                Hostname = "lan-client-01",
                DisplayName = "LAN Client",
                OsName = "Windows",
                Architecture = "x64",
                AgentVersion = "test",
                IpAddresses = ["192.168.20.15"],
                PublicKey = CreatePublicKey()
            });

            Assert.Equal("approved", response.Status);
            Assert.Equal(0, response.PollAfterSeconds);

            var client = await db.Clients
                .Include(c => c.Certificates)
                .SingleAsync(c => c.MachineId == machineId);
            Assert.Equal(ClientStatus.Online, client.Status);
            var certificate = Assert.Single(client.Certificates);
            Assert.Equal(CertificateStatus.Active, certificate.Status);
            Assert.Contains("BEGIN CERTIFICATE", certificate.CertificatePem);
            Assert.Equal(certificate.Thumbprint, client.CertificateThumbprint);
            Assert.True(await db.AuditLogs.AnyAsync(a =>
                a.Action == "agent.registration"
                && a.ResourceId == client.Id
                && a.Result == AuditResult.Success));
            Assert.True(File.Exists(bootstrap.SecretsPath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LanSimple_RejectsTokenlessRegistrationFromPublicNetwork()
    {
        var root = CreateTempRoot();
        try
        {
            var (configuration, _) = CreateLanConfiguration(root);
            await using var db = CreateDbContext();
            var context = new TestCurrentContext("8.8.8.8");
            var service = CreateService(db, configuration, context);

            var exception = await Assert.ThrowsAsync<BusinessException>(() => service.SubmitAsync(new SubmitRegistrationRequest
            {
                MachineId = $"public-{Guid.NewGuid():N}",
                Hostname = "public-client",
                PublicKey = CreatePublicKey()
            }));

            Assert.Equal("UNAUTHORIZED", exception.ErrorCode);
            Assert.Equal(401, exception.StatusCode);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task SecureMode_StillRequiresRegistrationToken()
    {
        var settings = new Dictionary<string, string?>
        {
            ["DeploymentMode"] = "Secure",
            ["LanMode:AutomaticEnrollment"] = "true",
            ["LanMode:PrivateNetworkOnly"] = "true"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        await using var db = CreateDbContext();
        var service = CreateService(db, configuration, new TestCurrentContext("127.0.0.1"));

        var exception = await Assert.ThrowsAsync<BusinessException>(() => service.SubmitAsync(new SubmitRegistrationRequest
        {
            MachineId = $"secure-{Guid.NewGuid():N}",
            Hostname = "secure-client",
            PublicKey = CreatePublicKey()
        }));

        Assert.Equal("UNAUTHORIZED", exception.ErrorCode);
        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public async Task LanSimple_InvalidPublicKey_RollsBackClientCreation()
    {
        var root = CreateTempRoot();
        var machineId = $"invalid-key-{Guid.NewGuid():N}";
        try
        {
            var (configuration, _) = CreateLanConfiguration(root);
            await using var db = CreateDbContext();
            var service = CreateService(db, configuration, new TestCurrentContext("10.10.10.10"));

            var exception = await Assert.ThrowsAsync<BusinessException>(() => service.SubmitAsync(new SubmitRegistrationRequest
            {
                MachineId = machineId,
                Hostname = "invalid-key-client",
                PublicKey = "not-a-public-key"
            }));

            Assert.Equal("INVALID_REQUEST", exception.ErrorCode);
            Assert.Equal(400, exception.StatusCode);
            Assert.False(await db.Clients.AnyAsync(c => c.MachineId == machineId));
            Assert.False(await db.ClientCertificates.AnyAsync(c => c.Client.MachineId == machineId));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LanSimple_ConcurrentSameMachine_AllowsOnlyOneActiveIdentity()
    {
        var root = CreateTempRoot();
        var machineId = $"concurrent-{Guid.NewGuid():N}";
        try
        {
            var (configuration, _) = CreateLanConfiguration(root);
            await using var db1 = CreateDbContext();
            await using var db2 = CreateDbContext();
            var service1 = CreateService(db1, configuration, new TestCurrentContext("172.16.1.10"));
            var service2 = CreateService(db2, configuration, new TestCurrentContext("172.16.1.11"));
            var request1 = CreateRegistrationRequest(machineId, "concurrent-client-1");
            var request2 = CreateRegistrationRequest(machineId, "concurrent-client-2");

            var outcomes = await Task.WhenAll(
                AttemptAsync(service1, request1),
                AttemptAsync(service2, request2));

            Assert.Single(outcomes, outcome => outcome.Response?.Status == "approved");
            var conflict = Assert.Single(outcomes, outcome => outcome.Exception is not null);
            Assert.Equal("CONFLICT", conflict.Exception!.ErrorCode);
            Assert.Equal(409, conflict.Exception.StatusCode);
            Assert.Equal(1, await db1.Clients.CountAsync(c => c.MachineId == machineId));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---------- A1：重注册的身份连续性 ----------

    /// <summary>
    /// 带正确签名重注册（自愈路径：ClearIdentity 保留了私钥）→ 行为与改动前完全一致：
    /// 直接 Online、签发证书、继承管理员设过的名字与分组。
    /// </summary>
    [Fact]
    public async Task 带连续性证明的重注册静默继承旧身份()
    {
        var root = CreateTempRoot();
        try
        {
            var (configuration, _) = CreateLanConfiguration(root);
            await using var db = CreateDbContext();
            var service = CreateService(db, configuration, new TestCurrentContext("192.168.20.15"));

            using var key = RSA.Create(2048);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var machineId = $"continuity-ok-{Guid.NewGuid():N}";

            await SeedOfflineClientAsync(db, service, machineId, publicKey, "财务服务器");

            var response = await service.SubmitAsync(new SubmitRegistrationRequest
            {
                MachineId = machineId,
                Hostname = "reinstalled-host",
                PublicKey = publicKey,
                ContinuityProof = SignMachineId(key, machineId),
                PreviousPublicKey = publicKey
            });

            Assert.Equal("approved", response.Status);

            var client = await db.Clients.Include(c => c.Certificates)
                .SingleAsync(c => c.Id == response.RegistrationId);
            Assert.Equal(ClientStatus.Online, client.Status);
            Assert.Equal("财务服务器", client.DisplayName);
            Assert.Single(client.Certificates);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    /// <summary>
    /// 不带签名（卸载重装，state.json 连同私钥一起没了）→ PendingApproval、**不签发证书**，
    /// 但名字与分组仍然继承——第三态是「交给人确认」，不是拒绝。
    ///
    /// 拒绝会把「重装后永远 409」的死锁装回来，那比冒名顶替更常见也更难查。
    /// </summary>
    [Fact]
    public async Task 无连续性证明的重注册落待审批且不签发证书()
    {
        var root = CreateTempRoot();
        try
        {
            var (configuration, _) = CreateLanConfiguration(root);
            await using var db = CreateDbContext();
            var service = CreateService(db, configuration, new TestCurrentContext("192.168.20.15"));

            var machineId = $"continuity-none-{Guid.NewGuid():N}";
            await SeedOfflineClientAsync(db, service, machineId, CreatePublicKey(), "财务服务器");

            var response = await service.SubmitAsync(new SubmitRegistrationRequest
            {
                MachineId = machineId,
                Hostname = "impostor-host",
                PublicKey = CreatePublicKey()
            });

            Assert.Equal("pending_approval", response.Status);

            var client = await db.Clients.Include(c => c.Certificates)
                .SingleAsync(c => c.Id == response.RegistrationId);
            Assert.Equal(ClientStatus.PendingApproval, client.Status);
            Assert.Empty(client.Certificates);
            Assert.Null(client.CertificateThumbprint);
            Assert.Equal("财务服务器", client.DisplayName);
            Assert.Contains("无法证明是同一台机器", client.Notes ?? "", StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    /// <summary>用另一把密钥签的证明与不带签名同等处理，且必须留下独立的审计记录。</summary>
    [Fact]
    public async Task 错误签名与无签名同等处理并写出审计()
    {
        var root = CreateTempRoot();
        try
        {
            var (configuration, _) = CreateLanConfiguration(root);
            await using var db = CreateDbContext();
            var service = CreateService(db, configuration, new TestCurrentContext("192.168.20.15"));

            var machineId = $"continuity-bad-{Guid.NewGuid():N}";
            await SeedOfflineClientAsync(db, service, machineId, CreatePublicKey(), "财务服务器");

            using var attacker = RSA.Create(2048);
            var attackerPublicKey = Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo());

            var response = await service.SubmitAsync(new SubmitRegistrationRequest
            {
                MachineId = machineId,
                Hostname = "impostor-host",
                PublicKey = attackerPublicKey,
                ContinuityProof = SignMachineId(attacker, machineId),
                PreviousPublicKey = attackerPublicKey
            });

            var client = await db.Clients.SingleAsync(c => c.Id == response.RegistrationId);
            Assert.Equal(ClientStatus.PendingApproval, client.Status);

            Assert.True(
                await db.AuditLogs.AnyAsync(a => a.Action == "agent.registration.identity_unproven"),
                "第三态必须留一条独立的审计记录——那是事后唯一能回答「那台机器什么时候被换掉」的地方");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    /// <summary>回归：活跃/疑似离线的旧身份仍然一律 409，原有的死锁修复不得被这条改动带偏。</summary>
    [Fact]
    public async Task 旧身份仍在线时依旧拒绝重注册()
    {
        var root = CreateTempRoot();
        try
        {
            var (configuration, _) = CreateLanConfiguration(root);
            await using var db = CreateDbContext();
            var service = CreateService(db, configuration, new TestCurrentContext("192.168.20.15"));

            using var key = RSA.Create(2048);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var machineId = $"continuity-online-{Guid.NewGuid():N}";

            await service.SubmitAsync(new SubmitRegistrationRequest
            {
                MachineId = machineId,
                Hostname = "original-host",
                PublicKey = publicKey
            });

            // 旧身份保持 Online：连正确的连续性证明也不能顶掉它。
            var exception = await Assert.ThrowsAsync<BusinessException>(() => service.SubmitAsync(
                new SubmitRegistrationRequest
                {
                    MachineId = machineId,
                    Hostname = "second-host",
                    PublicKey = publicKey,
                    ContinuityProof = SignMachineId(key, machineId),
                    PreviousPublicKey = publicKey
                }));

            Assert.Equal("CONFLICT", exception.ErrorCode);
            Assert.Equal(409, exception.StatusCode);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    /// <summary>先正常注册一台，再把它压成 Offline，模拟「服务端已确认掉线」。</summary>
    private static async Task SeedOfflineClientAsync(
        AppDbContext db, AgentRegistrationService service, string machineId, string publicKey, string displayName)
    {
        var first = await service.SubmitAsync(new SubmitRegistrationRequest
        {
            MachineId = machineId,
            Hostname = "original-host",
            DisplayName = displayName,
            PublicKey = publicKey
        });

        await db.Clients.Where(c => c.Id == first.RegistrationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ClientStatus.Offline));
        db.ChangeTracker.Clear();
    }

    private static string SignMachineId(RSA key, string machineId) =>
        Convert.ToBase64String(key.SignData(
            System.Text.Encoding.UTF8.GetBytes(machineId),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

    private AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options);

    private static AgentRegistrationService CreateService(
        AppDbContext db,
        IConfiguration configuration,
        ICurrentContext context) => new(
            db,
            new CertificateAuthority(configuration, NullLogger<CertificateAuthority>.Instance),
            new DbAuditRecorder(db, context, NullLogger<DbAuditRecorder>.Instance),
            context,
            configuration,
            NullLogger<AgentRegistrationService>.Instance);

    private static (IConfiguration Configuration, LocalServerBootstrapResult Bootstrap) CreateLanConfiguration(string root)
    {
        var baseConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeploymentMode"] = "LanSimple",
                ["LanMode:AutomaticEnrollment"] = "true",
                ["LanMode:PrivateNetworkOnly"] = "true",
                ["Postgres:Port"] = "55432",
                ["Server:DisplayName"] = "Test LAN Server"
            })
            .Build();
        var bootstrap = LocalServerBootstrap.Initialize(
            baseConfiguration,
            new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = root,
                SecretsPath = Path.Combine(root, "server-secrets.json"),
                EnforceAcl = false
            });
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(bootstrap.Values)
            .Build();
        return (configuration, bootstrap);
    }

    private static string CreatePublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    private static SubmitRegistrationRequest CreateRegistrationRequest(string machineId, string hostname) => new()
    {
        MachineId = machineId,
        Hostname = hostname,
        PublicKey = CreatePublicKey()
    };

    private static async Task<RegistrationAttempt> AttemptAsync(
        IAgentRegistrationService service,
        SubmitRegistrationRequest request)
    {
        try
        {
            return new RegistrationAttempt(await service.SubmitAsync(request), null);
        }
        catch (BusinessException exception)
        {
            return new RegistrationAttempt(null, exception);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BackupMonitor.AgentRegistrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // 测试临时文件清理失败不覆盖原始断言结果。
        }
    }

    private sealed class TestCurrentContext(string? clientIp) : ICurrentContext
    {
        public Guid? UserId => null;
        public string? Username => null;
        public Guid? ClientId => null;
        public string? ClientIp => clientIp;
        public string? UserAgent => "agent-registration-test";
        public string? RequestId => null;
    }

    private sealed record RegistrationAttempt(
        SubmitRegistrationResponse? Response,
        BusinessException? Exception);
}
