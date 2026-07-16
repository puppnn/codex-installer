using System.Diagnostics;
using System.Text.Json;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class CodexSystemService
{
    public static async Task<InstalledCodex?> GetInstalledAsync()
    {
        var installed = await GetInstalledPackageAsync("OpenAI.Codex");
        if (installed is null)
        {
            return null;
        }

        return new InstalledCodex(
            installed.Name,
            installed.PackageFullName,
            installed.Version,
            installed.Architecture,
            installed.InstallLocation);
    }

    public static async Task<InstalledStorePackage?> GetInstalledPackageAsync(string identityName)
    {
        return (await GetInstalledPackagesAsync(identityName))
            .OrderByDescending(package => package.Version)
            .FirstOrDefault();
    }

    public static async Task<IReadOnlyList<InstalledStorePackage>> GetInstalledPackagesAsync(
        string identityName)
    {
        var escapedName = EscapePowerShellLiteral(identityName);
        var command =
            $"Get-AppxPackage -Name '{escapedName}' | " +
            "Select-Object Name,PackageFullName,PackageFamilyName,Publisher,Version,Architecture,InstallLocation | " +
            "ConvertTo-Json -Compress";
        var result = await PowerShellRunner.RunAsync(command);
        if (!result.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException($"读取当前用户的已安装应用失败：{detail.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : root.ValueKind == JsonValueKind.Object
                ? [root]
                : [];
        var packages = new List<InstalledStorePackage>();
        foreach (var element in elements)
        {
            var versionText = ReadJsonString(element.GetProperty("Version"));
            if (!Version.TryParse(versionText, out var version)) continue;

            var packageFullName = ReadJsonString(element.GetProperty("PackageFullName"));
            packages.Add(new InstalledStorePackage(
                ReadJsonString(element.GetProperty("Name")),
                packageFullName,
                ReadJsonString(element.GetProperty("PackageFamilyName")),
                ReadJsonString(element.GetProperty("Publisher")),
                version,
                ReadArchitecture(element.GetProperty("Architecture"), packageFullName),
                ReadJsonString(element.GetProperty("InstallLocation"))));
        }

        return packages;
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
        return await InstallPackageAsync(packagePath, []);
    }

    public static async Task<ProcessRunResult> InstallPackageAsync(
        string packagePath,
        IReadOnlyList<string> dependencyPaths)
    {
        return await PowerShellRunner.RunAsync(BuildInstallCommand(packagePath, dependencyPaths));
    }

    internal static string BuildInstallCommand(
        string packagePath,
        IReadOnlyList<string> dependencyPaths)
    {
        var command = $"Add-AppxPackage -Path '{EscapePowerShellLiteral(packagePath)}'";
        if (dependencyPaths.Count > 0)
        {
            var dependencies = string.Join(
                ",",
                dependencyPaths.Select(path => $"'{EscapePowerShellLiteral(path)}'"));
            command += $" -DependencyPath @({dependencies})";
        }

        return command;
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
        foreach (var knownArchitecture in new[] { "arm64", "x64", "x86", "arm", "neutral" })
        {
            if (packageFullName.Contains($"_{knownArchitecture}_", StringComparison.OrdinalIgnoreCase))
            {
                return knownArchitecture;
            }
        }

        return architecture.ValueKind switch
        {
            JsonValueKind.String => (architecture.GetString() ?? "").ToLowerInvariant(),
            JsonValueKind.Number when architecture.TryGetInt32(out var value) => value switch
            {
                0 => "x86",
                5 => "arm",
                9 => "x64",
                11 => "neutral",
                12 => "arm64",
                _ => value.ToString(),
            },
            _ => "",
        };
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string ReadJsonString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString(),
        };
    }
}
