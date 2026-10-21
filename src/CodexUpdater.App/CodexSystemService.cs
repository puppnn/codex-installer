using System.ComponentModel;
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

    // Packaged services (for example Codex's sandbox service) run in session 0 and survive closing the app,
    // so FindRunningCodexProcesses cannot see them, yet Windows refuses to update the package while they run.
    public static async Task<IReadOnlyList<PackageServiceInfo>> FindPackageServicesAsync(
        string? installLocation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return [];
        var command =
            "Get-CimInstance -ClassName Win32_Service | " +
            $"Where-Object {{ $_.PathName -and $_.PathName.IndexOf('{EscapePowerShellLiteral(installLocation)}', [StringComparison]::OrdinalIgnoreCase) -ge 0 }} | " +
            "Select-Object Name,DisplayName,State,PathName | ConvertTo-Json -Compress";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ProcessRunResult result;
        try
        {
            result = await PowerShellRunner.RunAsync(command, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取应用后台服务超时，请稍后重试。");
        }
        if (!result.Succeeded)
            throw new InvalidOperationException($"读取应用后台服务失败：{result.ErrorMessage.Trim()}");
        if (string.IsNullOrWhiteSpace(result.StandardOutput)) return [];

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : root.ValueKind == JsonValueKind.Object
                ? [root]
                : [];
        return elements
            .Select(element => new PackageServiceInfo(
                ReadJsonString(element.GetProperty("Name")),
                ReadJsonString(element.GetProperty("DisplayName")),
                ReadJsonString(element.GetProperty("State")),
                ReadJsonString(element.GetProperty("PathName"))))
            .Where(service => service.Name.Length > 0 && PackageServiceInfo.IsInsidePackage(service.PathName, installLocation))
            .ToArray();
    }

    public static async Task<IReadOnlyList<DeploymentLogEntry>> ReadDeploymentLogAsync(
        string activityId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(activityId, out var id)) throw new ArgumentException("ActivityId 格式无效。", nameof(activityId));
        var command = $"Get-AppPackageLog -ActivityID '{id:D}' | Select-Object Id,Level,Message | ConvertTo-Json -Compress";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ProcessRunResult result;
        try
        {
            result = await PowerShellRunner.RunAsync(command, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取部署日志超时。");
        }
        if (!result.Succeeded) throw new InvalidOperationException(result.ErrorMessage.Trim());
        if (string.IsNullOrWhiteSpace(result.StandardOutput)) return [];

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : root.ValueKind == JsonValueKind.Object
                ? [root]
                : [];
        return elements
            .Select(element => new DeploymentLogEntry(
                int.TryParse(ReadJsonString(element.GetProperty("Id")), out var eventId) ? eventId : 0,
                int.TryParse(ReadJsonString(element.GetProperty("Level")), out var level) ? level : 0,
                ReadJsonString(element.GetProperty("Message"))))
            .ToArray();
    }

    public static Task StopServicesElevatedAsync(IReadOnlyList<PackageServiceInfo> services, CancellationToken cancellationToken = default) =>
        RunServiceCommandElevatedAsync(services, stop: true, cancellationToken);

    public static Task StartServicesElevatedAsync(IReadOnlyList<PackageServiceInfo> services, CancellationToken cancellationToken = default) =>
        RunServiceCommandElevatedAsync(services, stop: false, cancellationToken);

    internal static string BuildServiceScript(IReadOnlyList<PackageServiceInfo> services, bool stop)
    {
        if (services.Any(service => string.IsNullOrWhiteSpace(service.Name) || service.Name.IndexOfAny(['*', '?', '[', ']']) >= 0))
            throw new InvalidOperationException("服务名必须为明确的单个服务，不能使用通配符。");
        var records = string.Join(",", services.Select(service =>
            $"@{{ Name='{EscapePowerShellLiteral(service.Name)}'; Path='{EscapePowerShellLiteral(service.PathName)}' }}"));
        // sc.exe sends a single SCM request and refuses active dependents. It never recursively stops them.
        var script = $$"""
            $system = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
            $env:PSModulePath = [IO.Path]::Combine($system, 'WindowsPowerShell', 'v1.0', 'Modules')
            $sc = [IO.Path]::Combine($system, 'sc.exe')
            $targets = @({{records}})
            $stop = ${{stop.ToString().ToLowerInvariant()}}
            foreach ($target in $targets) {
                $entry = Get-CimInstance -ClassName Win32_Service -OperationTimeoutSec 10 |
                    Where-Object { $_.Name -eq $target.Name }
                if (-not $entry -or $entry.PathName -ne $target.Path) { exit 87 }
                $service = Get-Service -Name $target.Name
                if ($stop) {
                    if ($service.Status -ne 'Running') { exit 1061 }
                    if (@($service.DependentServices | Where-Object { $_.Status -ne 'Stopped' }).Count -gt 0) { exit 1051 }
                }
            }
            foreach ($target in $targets) {
                $service = Get-Service -Name $target.Name
                if ($stop) {
                    & $sc stop $target.Name | Out-Null
                    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
                } else {
                    if ($service.Status -eq 'StopPending') {
                        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
                    }
                    $service.Refresh()
                    if ($service.Status -eq 'Stopped') {
                        & $sc start $target.Name | Out-Null
                        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                    }
                    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))
                }
            }
            """;
        return PowerShellRunner.BuildScript(script);
    }

    private static async Task RunServiceCommandElevatedAsync(
        IReadOnlyList<PackageServiceInfo> services,
        bool stop,
        CancellationToken cancellationToken)
    {
        if (services.Count == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        var action = stop ? "停止" : "启动";
        var script = BuildServiceScript(services, stop);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            startInfo.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException($"已取消管理员授权，无法{action}后台服务：{string.Join("、", services.Select(service => service.Label))}。");
        }
        if (process is null) throw new InvalidOperationException($"无法以管理员身份{action}后台服务。");
        using (process)
        {
            _ = process.Handle;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Do not start restoration while an elevated helper could still issue stop requests.
                await process.WaitForExitAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"{action}后台服务超时：{string.Join("、", services.Select(service => service.Label))}。");
            }
            if (process.ExitCode == 1051)
                throw new InvalidOperationException("目标应用服务存在其他活动依赖服务，已拒绝连带停止。请在服务管理器中核对依赖关系后重试。");
            if (process.ExitCode == 87)
                throw new InvalidOperationException("服务身份或路径在授权前后发生变化，已停止操作。");
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{action}后台服务失败（退出码 {process.ExitCode}）：{string.Join("、", services.Select(service => service.Label))}。");
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
