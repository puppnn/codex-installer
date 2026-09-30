using System.Diagnostics;
using System.IO;
using System.Text;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class PowerShellRunner
{
    public static async Task<ProcessRunResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powerShellPath = Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powerShellPath))
        {
            throw new FileNotFoundException("找不到系统 Windows PowerShell。", powerShellPath);
        }

        var script = BuildScript(command);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            WorkingDirectory = systemDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        process.StartInfo.Environment["PSModulePath"] = Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "Modules");
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-OutputFormat");
        process.StartInfo.ArgumentList.Add("Text");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(encodedCommand);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch
            {
                // The process may already have exited.
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }

        return new ProcessRunResult(
            process.ExitCode,
            PowerShellOutputFormatter.Clean(await stdoutTask),
            PowerShellOutputFormatter.Clean(await stderrTask));
    }

    internal static string BuildScript(string command) => $$"""
        $ProgressPreference = 'SilentlyContinue'
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding = $utf8
        $OutputEncoding = $utf8
        $ErrorActionPreference = 'Stop'
        try {
            & {
        {{command}}
            }
            exit 0
        } catch {
            [Console]::Error.WriteLine(($_ | Out-String -Width 240).Trim())
            if ($_.Exception) { [Console]::Error.WriteLine($_.Exception.ToString()) }
            exit 1
        }
        """;
}

internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public string ErrorMessage => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardError;
}
