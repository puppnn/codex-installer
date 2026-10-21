using CodexUpdater.App;
using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class ServiceInstallationGuardTests
{
    private static readonly PackageServiceInfo[] Services = [new("Example.Service", "Example", "Running", "C:\\App\\service.exe")];
    private static ProcessRunResult Ok => new(0, "", "");

    [Fact]
    public async Task PartialStopFailure_RestoresBeforeReturningAndNeverInstalls()
    {
        var steps = new List<string>();
        var result = await ServiceInstallationGuard.RunAsync(Services,
            _ => { steps.Add("stop-partial"); throw new IOException("second service failed"); },
            () => { steps.Add("install"); return Task.FromResult(Ok); },
            success => { Assert.False(success); steps.Add("restore"); return Task.FromResult<string?>(null); }, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("second service failed", result.ErrorMessage);
        Assert.Equal(["stop-partial", "restore"], steps);
    }

    [Fact]
    public async Task InstallationException_RestoresServices()
    {
        var restored = false;
        var result = await ServiceInstallationGuard.RunAsync(Services, _ => Task.CompletedTask,
            () => throw new IOException("cannot start PowerShell"),
            success => { Assert.False(success); restored = true; return Task.FromResult<string?>(null); }, CancellationToken.None);
        Assert.True(restored);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task FailedInstallAndFailedRestore_PreservesBothErrors()
    {
        var result = await ServiceInstallationGuard.RunAsync(Services, _ => Task.CompletedTask,
            () => Task.FromResult(new ProcessRunResult(1, "", "deployment failed")),
            _ => throw new InvalidOperationException("UAC denied"), CancellationToken.None);
        Assert.Contains("deployment failed", result.ErrorMessage);
        Assert.Contains("UAC denied", result.ErrorMessage);
    }

    [Fact]
    public async Task SuccessfulInstallAndFailedRestore_ReturnsVisibleWarning()
    {
        var result = await ServiceInstallationGuard.RunAsync(Services, _ => Task.CompletedTask, () => Task.FromResult(Ok),
            success => { Assert.True(success); return Task.FromResult<string?>("service did not restart"); }, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Contains("service did not restart", result.RecoveryWarning);
    }

    [Fact]
    public async Task CancellationAfterStop_StillRestores()
    {
        using var token = new CancellationTokenSource();
        var restored = false;
        var installed = false;
        var result = await ServiceInstallationGuard.RunAsync(Services,
            _ => { token.Cancel(); return Task.CompletedTask; },
            () => { installed = true; return Task.FromResult(Ok); },
            _ => { restored = true; return Task.FromResult<string?>(null); }, token.Token);
        Assert.False(installed);
        Assert.True(restored);
        Assert.Contains("取消", result.ErrorMessage);
    }

    [Fact]
    public async Task NoServices_DoesNotRequestServiceOperations()
    {
        var result = await ServiceInstallationGuard.RunAsync([], _ => throw new Exception("unexpected stop"),
            () => Task.FromResult(Ok), _ => throw new Exception("unexpected restore"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task CancelledWait_DoesNotRestoreBeforeStopHelperSettles()
    {
        using var cancellation = new CancellationTokenSource();
        var helperExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = false;
        var task = ServiceInstallationGuard.RunAsync(Services, _ => helperExited.Task,
            () => throw new Exception("install must not start"),
            _ => { restored = true; return Task.FromResult<string?>(null); }, cancellation.Token);
        cancellation.Cancel();
        Assert.False(task.IsCompleted);
        Assert.False(restored);
        helperExited.SetResult();
        var result = await task;
        Assert.True(restored);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ServiceScripts_ParseWithoutExecutingServiceCommands()
    {
        foreach (var stop in new[] { true, false })
        {
            var script = CodexSystemService.BuildServiceScript(Services, stop);
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            var result = await PowerShellRunner.RunAsync(
                $"$null = [scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'))); Write-Output 'parsed'");
            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal("parsed", result.StandardOutput);
        }
    }

    [Fact]
    public void ServiceScript_UsesExactIdentityAndNonRecursiveScmStop()
    {
        var script = CodexSystemService.BuildServiceScript(Services, stop: true);
        Assert.DoesNotContain("-Force", script);
        Assert.Contains("$entry.PathName -ne $target.Path", script);
        Assert.Contains("exit 1051", script);
        Assert.Contains("& $sc stop $target.Name", script);
        Assert.Contains("$env:PSModulePath", script);
        Assert.Throws<InvalidOperationException>(() => CodexSystemService.BuildServiceScript(
            [Services[0] with { Name = "Example*" }], stop: true));
    }
}
