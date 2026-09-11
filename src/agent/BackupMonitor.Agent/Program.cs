using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables("BACKUPMONITOR_AGENT_");
builder.Services.Configure<BackupMonitor.Agent.AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddWindowsService(options => options.ServiceName = "BackupMonitor Agent");

// 事件日志之外再落一份纯文本日志。以服务方式运行时事件日志是唯一出路，
// 而现场排障需要能直接把文件发过来的东西。
builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider, BackupMonitor.Agent.FileLoggerProvider>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentStateStore>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentTrayNotificationStore>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentConfigStore>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentCandidateFileStore>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentApiClient>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentUpgradeCoordinator>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentSignatureVerifier>();
builder.Services.AddSingleton<BackupMonitor.Agent.SystemProbe>();
builder.Services.AddSingleton<BackupMonitor.Agent.AgentFileHashCache>();
// BackupScanner 的哈希缓存与并发度是可选构造参数（默认值即「关掉缓存、3 路并发」），
// 这里显式装配，好让配置里的 MaxParallelHashes 真的生效。
builder.Services.AddSingleton(sp => new BackupMonitor.Agent.BackupScanner(
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackupMonitor.Agent.BackupScanner>>(),
    sp.GetRequiredService<BackupMonitor.Agent.AgentFileHashCache>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupMonitor.Agent.AgentOptions>>().Value.MaxParallelHashes));
builder.Services.AddSingleton<BackupMonitor.Agent.DirectoryBrowser>();
builder.Services.AddSingleton<BackupMonitor.Agent.BackupDirectoryProber>();
builder.Services.AddSingleton<BackupMonitor.Agent.InstalledServiceProbe>();
builder.Services.AddHostedService<BackupMonitor.Agent.AgentWorker>();

await builder.Build().RunAsync();
