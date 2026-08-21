using System.Diagnostics;

namespace BackupMonitor.Server.Setup;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool throwOnError = true)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
                process.StartInfo.Environment[pair.Key] = pair.Value;
        }

        if (!process.Start())
            throw new InvalidOperationException($"无法启动系统工具：{fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var result = new ProcessResult(process.ExitCode, stdout, stderr);
        if (throwOnError && result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException(
                $"系统工具 {Path.GetFileName(fileName)} 退出码 {result.ExitCode}：{detail}");
        }

        return result;
    }
}
