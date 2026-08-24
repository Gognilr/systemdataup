using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupMonitor.Agent;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 客户端证书是在 TLS 握手阶段协商的，握手之后连接的身份就固定了。
///
/// Agent 的注册流程必然先以「没有证书」的身份建立连接（提交申请、轮询审批结果都是匿名端点），
/// 审批通过拿到证书后，才开始调用 mTLS 端点。如果此时复用的还是注册阶段那条连接，
/// 服务端看到的 Connection.ClientCertificate 永远是 null，心跳一律 401；
/// 而失败重试间隔短于连接池空闲回收时间，这条连接不会被淘汰——Agent 永久卡死在
/// 「注册成功、零心跳」的状态，页面上表现为客户端在线但所有指标为空。
///
/// 因此挂载证书时必须重建连接池。这里用真实的 Kestrel + mTLS 端点验证。
/// </summary>
public sealed class AgentClientCertificateHandshakeTests : IAsyncLifetime
{
    private readonly List<(string Path, string ConnectionId, string? Thumbprint)> _observed = [];
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "bm-agent-mtls-" + Guid.NewGuid().ToString("N"));

    private X509Certificate2 _serverCertificate = null!;
    private WebApplication _app = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        _serverCertificate = CreateCertificate("CN=localhost", serverAuth: true);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigurationLoader = null;
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = _serverCertificate;
                // 与生产一致：请求但不强制客户端证书，链校验交给指纹查表，这里直接放行。
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.ClientCertificateValidation = static (_, _, _) => true;
            }));
        });

        _app = builder.Build();
        _app.Run(async context =>
        {
            lock (_observed)
            {
                _observed.Add((
                    context.Request.Path.Value ?? string.Empty,
                    context.Connection.Id,
                    context.Connection.ClientCertificate?.Thumbprint));
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"success":true,"data":{}}""");
        });

        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _serverCertificate.Dispose();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }

    [Fact]
    public async Task 注册后挂载的证书必须对后续请求生效()
    {
        using var clientCertificate = CreateCertificate("CN=bm-agent-test", serverAuth: false);
        using var api = CreateApiClient();

        // 1. 注册阶段：还没有证书，连接以匿名身份握手。
        await api.GetRegistrationResultAsync(Guid.NewGuid(), CancellationToken.None);

        // 2. 对照组：证书没有变化时连接会被复用——证明连接池确实在起作用，
        //    否则第 3 步的断言可能只是因为每次请求都新建连接而侥幸通过。
        await api.GetRegistrationResultAsync(Guid.NewGuid(), CancellationToken.None);

        // 3. 审批通过，挂载证书，随后调用 mTLS 端点。
        api.AttachCertificate(clientCertificate);
        await api.HeartbeatAsync(new HeartbeatRequest(), CancellationToken.None);

        (string Path, string ConnectionId, string? Thumbprint)[] observed;
        lock (_observed)
        {
            observed = _observed.ToArray();
        }

        Assert.Equal(3, observed.Length);
        Assert.Null(observed[0].Thumbprint);
        Assert.Null(observed[1].Thumbprint);
        Assert.Equal(observed[0].ConnectionId, observed[1].ConnectionId);

        Assert.EndsWith("/heartbeat", observed[2].Path, StringComparison.Ordinal);
        Assert.Equal(clientCertificate.Thumbprint, observed[2].Thumbprint);
    }

    [Fact]
    public async Task 重新握手不会丢掉已挂载的证书()
    {
        using var clientCertificate = CreateCertificate("CN=bm-agent-test", serverAuth: false);
        using var api = CreateApiClient();

        api.AttachCertificate(clientCertificate);
        await api.HeartbeatAsync(new HeartbeatRequest(), CancellationToken.None);

        // 401 自愈路径：丢弃连接池重新握手，证书必须保持不变。
        api.RecycleConnections();
        await api.HeartbeatAsync(new HeartbeatRequest(), CancellationToken.None);

        (string Path, string ConnectionId, string? Thumbprint)[] observed;
        lock (_observed)
        {
            observed = _observed.ToArray();
        }

        Assert.Equal(2, observed.Length);
        Assert.All(observed, entry => Assert.Equal(clientCertificate.Thumbprint, entry.Thumbprint));
        Assert.NotEqual(observed[0].ConnectionId, observed[1].ConnectionId);
    }

    private AgentApiClient CreateApiClient()
    {
        var options = Options.Create(new AgentOptions
        {
            ServerUrl = _baseUrl,
            DataDirectory = _dataDirectory,
            EnforceAcl = false,
            AllowInsecureTls = true
        });

        var stateStore = new AgentStateStore(options, NullLogger<AgentStateStore>.Instance);
        return new AgentApiClient(options, stateStore);
    }

    /// <summary>
    /// 自签证书。必须经 PFX 往返重新导入：CreateSelfSigned 返回的临时密钥
    /// 在 Windows 上无法用于 SChannel 的 TLS 握手。
    /// </summary>
    private static X509Certificate2 CreateCertificate(string subject, bool serverAuth)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], true));
        if (serverAuth)
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        var now = DateTimeOffset.UtcNow;
        using var selfSigned = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        const string password = "bm-test";
        return new X509Certificate2(
            selfSigned.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
}
