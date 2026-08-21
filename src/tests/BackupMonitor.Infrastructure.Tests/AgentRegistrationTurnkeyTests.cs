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
