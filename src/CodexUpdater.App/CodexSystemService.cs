using System.Diagnostics;
using System.Text.Json;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class CodexSystemService
{
    public static async Task<InstalledCodex?> GetInstalledAsync()
    {
        const string command =
            "Get-AppxPackage -Name OpenAI.Codex | " +
            "Select-Object Name,PackageFullName,Version,Architecture,InstallLocation | " +
            "ConvertTo-Json -Compress";

        var result = await PowerShellRunner.RunAsync(command);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            root = root.EnumerateArray().FirstOrDefault();
        }

        if (root.ValueKind != JsonValueKind.Object) return null;

        var versionText = root.GetProperty("Version").GetString();
        if (!Version.TryParse(versionText, out var version)) return null;

        var packageFullName = root.GetProperty("PackageFullName").GetString() ?? "";

        return new InstalledCodex(
            root.GetProperty("Name").GetString() ?? "OpenAI.Codex",
            packageFullName,
            version,
            ReadArchitecture(root.GetProperty("Architecture"), packageFullName),
            root.GetProperty("InstallLocation").GetString() ?? "");
    }

    public static IReadOnlyList<Process> FindRunningCodexProcesses()
    {
        var currentId = Environment.ProcessId;
        return Process.GetProcesses()
            .Where(process =>
            {
                try
                {
                    return process.Id != currentId &&
                        string.Equals(process.ProcessName, "Codex", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            })
            .ToArray();
    }

    public static async Task CloseCodexAsync(IReadOnlyList<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.CloseMainWindow();
            }
            catch
            {
                // The process may already have exited or may not expose a main window.
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (processes.All(HasExited)) return;
            await Task.Delay(300);
        }

        foreach (var process in processes.Where(process => !HasExited(process)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Installation will report a precise package-in-use error if closing failed.
            }
        }
    }

    public static async Task<ProcessRunResult> InstallPackageAsync(string packagePath)
    {
        var escapedPath = packagePath.Replace("'", "''", StringComparison.Ordinal);
        return await PowerShellRunner.RunAsync($"Add-AppxPackage -Path '{escapedPath}'");
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static string ReadArchitecture(JsonElement architecture, string packageFullName)
    {
        if (packageFullName.Contains("_arm64__", StringComparison.OrdinalIgnoreCase) ||
            packageFullName.Contains("_arm64_", StringComparison.OrdinalIgnoreCase))
        {
            return "arm64";
        }

        if (packageFullName.Contains("_x64__", StringComparison.OrdinalIgnoreCase) ||
            packageFullName.Contains("_x64_", StringComparison.OrdinalIgnoreCase))
        {
            return "x64";
        }

        return architecture.ValueKind switch
        {
            JsonValueKind.String => architecture.GetString() ?? "",
            JsonValueKind.Number when architecture.TryGetInt32(out var value) => value switch
            {
                9 => "x64",
                12 => "arm64",
                5 => "arm64",
                2 => "x64",
                _ => value.ToString(),
            },
            _ => "",
        };
    }
}
