param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [string]$ExpectedInstalledVersion,
    [int]$TimeoutSeconds = 40
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute = $false
$start.RedirectStandardError = $true
$start.WorkingDirectory = Split-Path -Parent $executable
# Use the normal bundle location, not a cache override inherited from a build shell.
$null = $start.EnvironmentVariables.Remove('DOTNET_BUNDLE_EXTRACT_BASE_DIR')
$application = [Diagnostics.Process]::Start($start)
$started = $application.StartTime
$stderr = $application.StandardError.ReadToEndAsync()
$result = $null
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($application.HasExited) {
            throw "Application exited ($($application.ExitCode)): $($stderr.GetAwaiter().GetResult())"
        }
        $application.Refresh()
        if ($application.MainWindowHandle -ne 0 -and $application.MainWindowTitle -like 'Codex*' -and $application.Responding) {
            $window = [Windows.Automation.AutomationElement]::FromHandle($application.MainWindowHandle)
            $condition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::AutomationIdProperty, 'RouteSelector')
            $control = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $control) {
                $version = $null
                if ($ExpectedInstalledVersion) {
                    $versionCondition = [Windows.Automation.PropertyCondition]::new(
                        [Windows.Automation.AutomationElement]::NameProperty, $ExpectedInstalledVersion)
                    $version = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $versionCondition)
                }
                if (-not $ExpectedInstalledVersion -or $null -ne $version) {
                    $result = [pscustomobject]@{
                        Executable = $executable
                        ApplicationPid = $application.Id
                        Started = $started
                        WindowTitle = $application.MainWindowTitle
                        DisplayedInstalledVersion = if ($version) { $version.Current.Name } else { $null }
                        StartupPassed = $true
                    }
                    break
                }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $result) {
        throw "No responsive application window with expected version '$ExpectedInstalledVersion' within $TimeoutSeconds seconds."
    }
    if (-not $application.CloseMainWindow() -or -not $application.WaitForExit(10000)) {
        throw 'The application did not close cleanly.'
    }
    if ($application.ExitCode -ne 0) { throw "The application exited with code $($application.ExitCode)." }
    $result
} finally {
    # The held Process handle belongs only to this invocation, never another user's window.
    if (-not $application.HasExited) {
        $null = $application.CloseMainWindow()
        if (-not $application.WaitForExit(3000)) { $application.Kill(); $application.WaitForExit() }
    }
    $application.Dispose()
}
