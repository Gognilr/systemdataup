using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using BackupMonitor.Api.Discovery;
using BackupMonitor.Shared.Models.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMonitor.Infrastructure.Tests;

public sealed class LanDiscoveryTests
{
    [Fact]
    public async Task DiscoveryRespondsWithPublicMetadataAndMatchingNonce()
    {
        var port = GetFreeUdpPort();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanMode:DiscoveryEnabled"] = "true",
                ["LanMode:DiscoveryPort"] = port.ToString(),
                ["LanMode:AdvertisedUrl"] = "http://server.example.invalid:5080",
                ["Server:InstanceId"] = "server-instance-1",
                ["Server:DisplayName"] = "局域网备份服务器"
            })
            .Build();
        var service = new LanDiscoveryHostedService(
            configuration,
            NullLogger<LanDiscoveryHostedService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var request = new LanDiscoveryRequest { Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) };
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await service.StartAsync(timeout.Token);
        try
        {
            await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
            var result = await client.ReceiveAsync(timeout.Token);
            var response = JsonSerializer.Deserialize<LanDiscoveryResponse>(
                result.Buffer,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(response);
            Assert.Equal(LanDiscoveryProtocol.Name, response!.Protocol);
            Assert.Equal(LanDiscoveryProtocol.Version, response.ProtocolVersion);
            Assert.Equal(request.Nonce, response.Nonce);
            Assert.Equal("server-instance-1", response.ServerInstanceId);
            Assert.Equal("局域网备份服务器", response.DisplayName);
            Assert.Equal("http://server.example.invalid:5080", response.ApiAddress);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DiscoveryPortConflictDoesNotStopTheHostedServiceProcess()
    {
        var port = GetFreeUdpPort();
        using var blocker = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanMode:DiscoveryEnabled"] = "true",
                ["LanMode:DiscoveryPort"] = port.ToString()
            })
            .Build();
        var service = new LanDiscoveryHostedService(
            configuration,
            NullLogger<LanDiscoveryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
