using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 证书到期巡检（整改清单 R9 第 1 点 / R10）。
///
/// 三种证书里只有客户端证书原本就是完整闭环。另外两种既不续签也不提醒：
/// 服务端 TLS 证书过期了照样加载照样用；客户端 CA 到第 9 年起会把新签的
/// 客户端证书越截越短，到第 10 年全体 Agent 同一天失效——
/// 而这两件事在此之前**一条日志都不会留**。
///
/// 这几条钉的是「该说话的时候说了，不该说话的时候闭嘴」：
/// 到期还早的证书不许报警（报了就没人信了），快到期的必须报。
/// </summary>
[Collection("postgres")]
public class CertificateLifecycleTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;
    private string _workDirectory = null!;

    public CertificateLifecycleTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _workDirectory = Path.Combine(
            Path.GetTempPath(), "BackupMonitor.CertLifecycleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_workDirectory))
                Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }

    /// <summary>还有两年才到期的证书不许报警——假警报会让真警报也没人看。</summary>
    [Fact]
    public async Task 有效期充足时不报警()
    {
        var password = "cert-test-pw";
        var serverPath = WriteCertificate("server-ok.pfx", password, DateTimeOffset.UtcNow.AddYears(2));

        await using var services = BuildServices(serverPath, password, caPath: null, caPassword: null);
        await RunAsync(services);

        Assert.Null(await FindAlertAsync(services, "server_certificate_expiring"));
    }

    /// <summary>剩 10 天：≤14 天是严重档，处置它需要一个有准备的过渡期，不是当天能做完的事。</summary>
    [Fact]
    public async Task 服务端证书临近到期时报严重告警()
    {
        var password = "cert-test-pw";
        var serverPath = WriteCertificate("server-expiring.pfx", password, DateTimeOffset.UtcNow.AddDays(10));

        await using var services = BuildServices(serverPath, password, caPath: null, caPassword: null);
        await RunAsync(services);

        var alert = await FindAlertAsync(services, "server_certificate_expiring");
        Assert.NotNull(alert);
        Assert.Equal(AlertLevel.Critical, alert!.Value.Level);
        // 正文必须把「不要直接重新签发」写出来：那一步会让全网 Agent 掉线
        Assert.Contains("预备新证书", alert.Value.Message);
    }

    /// <summary>
    /// CA 只剩 200 天而客户端证书是 365 天：从这一刻起每一张新签的客户端证书
    /// 都会被截短，而且一次比一次短。这是 CA 到期最早的可观测信号。
    /// </summary>
    [Fact]
    public async Task CA剩余寿命短于客户端证书有效期时报截断告警()
    {
        var password = "cert-test-pw";
        var caPath = WriteCertificate("ca-short.pfx", password, DateTimeOffset.UtcNow.AddDays(200));

        await using var services = BuildServices(
            serverPath: null, serverPassword: null, caPath: caPath, caPassword: password);
        await RunAsync(services);

        var alert = await FindAlertAsync(services, "client_certificate_truncated");
        Assert.NotNull(alert);
        Assert.Contains("截短", alert!.Value.Message);
    }

    /// <summary>CA 还有 5 年：截断还早得很，不该说话。</summary>
    [Fact]
    public async Task CA有效期充足时不报截断告警()
    {
        var password = "cert-test-pw";
        var caPath = WriteCertificate("ca-long.pfx", password, DateTimeOffset.UtcNow.AddYears(5));

        await using var services = BuildServices(
            serverPath: null, serverPassword: null, caPath: caPath, caPassword: password);
        await RunAsync(services);

        Assert.Null(await FindAlertAsync(services, "client_certificate_truncated"));
    }

    // ---------- 基础设施 ----------

    private string WriteCertificate(string fileName, string password, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=BackupMonitor Cert Lifecycle Test {Guid.NewGuid():N}",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), notAfter);

        var path = Path.Combine(_workDirectory, fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static async Task RunAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICertificateLifecycleChecker>().CheckAsync();
    }

    /// <summary>
    /// 按 category 取本次用例造出来的那条告警。
    /// 整个 postgres 集合共用一个库，但这几个 category 只有这一组用例会产生。
    /// </summary>
    private static async Task<(AlertLevel Level, string Message)?> FindAlertAsync(
        ServiceProvider services, string category)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Alerts.AsNoTracking()
            .Where(a => a.Category == category && a.Status != AlertStatus.Recovered)
            .OrderByDescending(a => a.LastOccurredAt)
            .Select(a => new { a.Level, a.Message })
            .FirstOrDefaultAsync();
        return row is null ? null : (row.Level, row.Message ?? string.Empty);
    }

    private ServiceProvider BuildServices(
        string? serverPath, string? serverPassword, string? caPath, string? caPassword)
    {
        var settings = new Dictionary<string, string?>();
        if (serverPath is not null)
        {
            settings["Security:ServerCertificate:CertPath"] = serverPath;
            settings["Security:ServerCertificate:Password"] = serverPassword;
        }
        if (caPath is not null)
        {
            settings["Security:ClientCa:CertPath"] = caPath;
            settings["Security:ClientCa:Password"] = caPassword;
        }

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        sc.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        sc.AddSingleton<ICurrentContext, NullCurrentContext>();
        sc.AddScoped<IAuditRecorder, DbAuditRecorder>();
        sc.AddScoped<IAgentNotificationService, AgentNotificationService>();
        sc.AddScoped<IAlertingService, AlertingService>();
        sc.AddScoped<ICertificateLifecycleChecker, CertificateLifecycleChecker>();
        sc.AddSingleton(sp => new SystemSettingsProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SystemSettingsProvider>>()));
        sc.AddSingleton(NullLogger<CertificateLifecycleChecker>.Instance);
        return sc.BuildServiceProvider();
    }
}
