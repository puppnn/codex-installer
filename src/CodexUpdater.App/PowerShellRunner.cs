using System.Diagnostics;
using System.Text;

namespace CodexUpdater.App;

internal static class PowerShellRunner
{
    public static async Task<ProcessRunResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command {Quote(command)}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new ProcessRunResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string Quote(string value)
    {
        return '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }
}

internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
