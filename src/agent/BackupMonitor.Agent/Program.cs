using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables("BACKUPMONITOR_AGENT_");
builder.Services.Configure<BackupMonitor.Agent.AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddWindowsService(options => options.ServiceName = "BackupMonitor Agent");
builder.Services.AddSingleton<BackupMonitor.Agent.AgentStateStore>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentTrayNotificationStore>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentConfigStore>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentApiClient>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentSignatureVerifier>();
builder.Services.AddSingleton<BackupMonitor.Agent.SystemProbe>();
builder.Services.AddSingleton<BackupMonitor.Agent.BackupScanner>();
builder.Services.AddHostedService<BackupMonitor.Agent.AgentWorker>();

await builder.Build().RunAsync();
