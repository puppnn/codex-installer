using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class CodexSystemService
{
    public static async Task<InstalledCodex?> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        var installed = (await GetInstalledPackagesAsync("OpenAI.Codex", cancellationToken))
            .Where(package => package.PackageFamilyName.Equals(
                $"{CodexPackage.PackagePrefix}_{CodexPackage.PublisherId}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(package => package.Version)
            .FirstOrDefault();
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
        string identityName,
        CancellationToken cancellationToken = default)
    {
        var escapedName = EscapePowerShellLiteral(identityName);
        var command =
            $"Get-AppxPackage -Name '{escapedName}' | " +
            "Select-Object Name,PackageFullName,PackageFamilyName,Publisher,Version,Architecture,InstallLocation | " +
            "ConvertTo-Json -Compress";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ProcessRunResult result;
        try
        {
            result = await PowerShellRunner.RunAsync(command, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取已安装应用超时，请检查 Windows 应用部署服务后重试。");
        }
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

    public static IReadOnlyList<Process> FindRunningCodexProcesses(string? installLocation = null)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return [];
        var packageDirectory = Path.GetFullPath(installLocation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var packageStore = Directory.GetParent(packageDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName + Path.DirectorySeparatorChar;
        using var currentProcess = Process.GetCurrentProcess();
        var sessionId = currentProcess.SessionId;
        var currentId = Environment.ProcessId;
        return Process.GetProcesses()
            .Where(process =>
            {
                try
                {
                    var matches = process.Id != currentId && process.SessionId == sessionId &&
                        process.MainModule?.FileName is { } fileName &&
                        (fileName.StartsWith(packageDirectory, StringComparison.OrdinalIgnoreCase) ||
                         (fileName.StartsWith(packageStore, StringComparison.OrdinalIgnoreCase) && IsCodexPackageProcess(process)));
                    if (matches)
                    {
                        _ = process.Handle;
                        _ = process.StartTime;
                        matches = !process.HasExited;
                    }
                    if (!matches) process.Dispose();
                    return matches;
                }
                catch
                {
                    process.Dispose();
                    return false;
                }
            })
            .ToArray();
    }

    private static bool IsCodexPackageProcess(Process process)
    {
        uint length = 0;
        if (GetPackageFamilyName(process.Handle, ref length, null) != 122 || length is 0 or > 512) return false;
        var family = new StringBuilder((int)length);
        return GetPackageFamilyName(process.Handle, ref length, family) == 0 &&
            family.ToString().Equals($"{CodexPackage.PackagePrefix}_{CodexPackage.PublisherId}", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? familyName);

    public static async Task CloseCodexAsync(IReadOnlyList<Process> processes, CancellationToken cancellationToken = default)
    {
        foreach (var process in processes)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!HasExited(process)) process.CloseMainWindow();
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // The process may already have exited or may not expose a main window.
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (processes.All(HasExited)) return;
            await Task.Delay(300, cancellationToken);
        }

        foreach (var process in processes.Where(process => !HasExited(process)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                process.Kill();
            }
            catch
            {
                // Installation will report a precise package-in-use error if closing failed.
            }
        }
        try
        {
            await Task.WhenAll(processes.Where(process => !HasExited(process)).Select(process => process.WaitForExitAsync(cancellationToken)))
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (TimeoutException)
        {
            // Windows deployment returns the package-in-use error if a process still holds files.
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
