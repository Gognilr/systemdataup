using System.Net;
using BackupMonitor.Api.Bootstrap;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Configuration;

namespace BackupMonitor.Infrastructure.Tests;

public sealed class LocalServerBootstrapTests
{
    [Fact]
    public void First_start_generates_persists_and_reuses_all_internal_secrets()
    {
        var root = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationBuilder().Build();
            var options = new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = root,
                SecretsPath = Path.Combine(root, "server-secrets.json"),
                DiscoveryEnabled = true,
                EnforceAcl = false
            };

            var first = LocalServerBootstrap.Initialize(configuration, options);
            var firstSecrets = File.ReadAllText(first.SecretsPath);
            var firstInstance = first.InstanceId;
            var firstJwt = first.Values["Security:Jwt:SigningKey"];
            var firstCommandKey = first.Values["Security:CommandSigningPrivateKey"];

            var second = LocalServerBootstrap.Initialize(configuration, options);

            Assert.True(first.Enabled);
            Assert.True(File.Exists(first.SecretsPath));
            Assert.True(File.Exists(Path.Combine(root, "client-ca.pfx")));
            Assert.Equal(firstInstance, second.InstanceId);
            Assert.Equal(firstJwt, second.Values["Security:Jwt:SigningKey"]);
            Assert.Equal(firstCommandKey, second.Values["Security:CommandSigningPrivateKey"]);
            Assert.Equal(firstSecrets, File.ReadAllText(second.SecretsPath));
            Assert.Equal("LanSimple", second.Values["DeploymentMode"]);
            Assert.Equal("true", second.Values["LanMode:AutomaticEnrollment"]);

            var installationValues = LocalServerBootstrap.Initialize(
                configuration,
                new LocalServerBootstrapOptions
                {
                    Enabled = true,
                    DataDirectory = root,
                    SecretsPath = Path.Combine(root, "server-secrets.json"),
                    IncludeInstallationSecrets = true,
                    EnforceAcl = false
                });
            Assert.NotEqual(
                installationValues.Values["Postgres:Password"],
                installationValues.Values["Postgres:SuperuserPassword"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Explicit_configuration_can_disable_lan_automatic_enrollment()
    {
        var root = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DeploymentMode"] = "Secure",
                    ["LanMode:AutomaticEnrollment"] = "false",
                    ["LanMode:PrivateNetworkOnly"] = "true",
                    ["LanMode:EnrollmentOpenUntil"] = DateTime.UtcNow.AddHours(1).ToString("O")
                })
                .Build();

            var result = LocalServerBootstrap.Initialize(configuration, new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = root,
                SecretsPath = Path.Combine(root, "server-secrets.json"),
                EnforceAcl = false
            });

            Assert.Equal("Secure", result.Values["DeploymentMode"]);
            Assert.Equal("false", result.Values["LanMode:AutomaticEnrollment"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Existing_ca_without_matching_secrets_fails_with_recovery_guidance()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = root,
                SecretsPath = Path.Combine(root, "server-secrets.json"),
                EnforceAcl = false
            };
            LocalServerBootstrap.Initialize(new ConfigurationBuilder().Build(), options);
            File.Delete(options.SecretsPath!);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                LocalServerBootstrap.Initialize(new ConfigurationBuilder().Build(), options));
            Assert.Contains("client-ca.pfx", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("server-secrets.json", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Disabled_bootstrap_does_not_create_program_data_files()
    {
        var root = CreateTempDirectory();
        try
        {
            var result = LocalServerBootstrap.Initialize(
                new ConfigurationBuilder().Build(),
                new LocalServerBootstrapOptions { Enabled = false, DataDirectory = root });

            Assert.False(result.Enabled);
            Assert.Empty(result.Values);
            Assert.False(File.Exists(result.SecretsPath));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.10.1.8", true)]
    [InlineData("172.16.1.8", true)]
    [InlineData("192.168.1.8", true)]
    [InlineData("169.254.10.8", true)]
    [InlineData("::ffff:10.10.1.8", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Lan_policy_accepts_only_private_or_loopback_addresses(string value, bool expected)
    {
        Assert.Equal(expected, LanNetworkPolicy.IsPrivateOrLoopback(IPAddress.Parse(value)));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "BackupMonitor.LocalBootstrapTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 测试临时目录清理失败不掩盖断言结果。
        }
    }
}
