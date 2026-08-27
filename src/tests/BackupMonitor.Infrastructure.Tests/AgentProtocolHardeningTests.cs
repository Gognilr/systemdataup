using System.Text;
using BackupMonitor.Agent;
using BackupMonitor.Shared.Models.Agent;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupMonitor.Infrastructure.Tests;

public sealed class AgentProtocolHardeningTests
{
    [Fact]
    public void Command_signature_bytes_are_independent_of_datetime_kind()
    {
        var instant = new DateTime(2026, 8, 13, 12, 34, 56, 789, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(instant, DateTimeKind.Unspecified);
        var local = instant.ToLocalTime();

        var utcPayload = AgentSignatureCanonicalizer.CommandPayload(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "nonce-1",
            "refresh_metrics",
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            instant,
            taskId: null,
            candidateBackupSetId: null,
            payload: null);
        var unspecifiedPayload = AgentSignatureCanonicalizer.CommandPayload(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "nonce-1",
            "refresh_metrics",
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            unspecified,
            taskId: null,
            candidateBackupSetId: null,
            payload: null);
        var localPayload = AgentSignatureCanonicalizer.CommandPayload(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "nonce-1",
            "refresh_metrics",
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            local,
            taskId: null,
            candidateBackupSetId: null,
            payload: null);

        Assert.Equal(utcPayload, unspecifiedPayload);
        Assert.Equal(utcPayload, localPayload);
        Assert.StartsWith("v2|", Encoding.UTF8.GetString(utcPayload));
        var timestamp = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                AgentSignatureCanonicalizer.ToUnixMilliseconds(instant).ToString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        Assert.Contains(timestamp, Encoding.UTF8.GetString(utcPayload));
    }

    [Fact]
    public void Config_signature_bytes_are_stable_when_input_collections_are_reordered()
    {
        var taskA = new AgentTaskConfigDto
        {
            TaskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Name = "A",
            ApplicationName = "App",
            SourcePath = "C:\\A",
            RecognizerType = "latest_single_file",
            TaskMode = "automatic",
            Enabled = true,
            ChunkSizeBytes = 4 * 1024 * 1024,
            RecognizerConfig = "{}",
            ConfigVersion = 1
        };
        var taskB = new AgentTaskConfigDto
        {
            TaskId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Name = "B",
            ApplicationName = "App",
            SourcePath = "C:\\B",
            RecognizerType = "latest_single_file",
            TaskMode = "automatic",
            Enabled = true,
            ChunkSizeBytes = 4 * 1024 * 1024,
            RecognizerConfig = "{}",
            ConfigVersion = 1
        };
        var serviceA = new AgentMonitoredServiceDto { ServiceName = "svc-a", DisplayName = "A" };
        var serviceB = new AgentMonitoredServiceDto { ServiceName = "svc-b", DisplayName = "B" };
        var settings = new AgentGlobalSettingsDto { HeartbeatIntervalSeconds = 60, MaxConcurrentUploads = 2 };

        var first = AgentSignatureCanonicalizer.ConfigPayload(
            3,
            taskA.TaskId,
            [taskB, taskA],
            [serviceB, serviceA],
            settings);
        var second = AgentSignatureCanonicalizer.ConfigPayload(
            3,
            taskA.TaskId,
            [taskA, taskB],
            [serviceA, serviceB],
            settings);

        Assert.Equal(first, second);
        Assert.StartsWith("v1|", Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void State_scalar_properties_do_not_require_snapshot_deep_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "BackupMonitor.AgentProtocolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new AgentOptions { DataDirectory = root, EnforceAcl = false });
            var store = new AgentStateStore(options, NullLogger<AgentStateStore>.Instance);
            var clientId = Guid.NewGuid();
            store.Update(state =>
            {
                state.ClientId = clientId;
                state.MachineId = "machine-1";
                state.ConfigVersion = 7;
                state.Candidates["large"] = new LocalCandidateState
                {
                    Files = Enumerable.Range(0, 10_000)
                        .Select(i => new LocalCandidateFile { RelativePath = $"{i}.dat" })
                        .ToList()
                };
            });

            Assert.Equal(clientId, store.ClientId);
            Assert.Equal("machine-1", store.MachineId);
            Assert.Equal(7, store.ConfigVersion);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
