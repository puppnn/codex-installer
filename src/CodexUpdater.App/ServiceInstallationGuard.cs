using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class ServiceInstallationGuard
{
    public static async Task<ProcessRunResult> RunAsync(
        IReadOnlyList<PackageServiceInfo> services,
        Func<CancellationToken, Task> stop,
        Func<Task<ProcessRunResult>> install,
        Func<bool, Task<string?>> restore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessRunResult result = new(-1, "", "安装未执行。");
        var stopAttempted = false;
        string? recoveryWarning = null;
        try
        {
            if (services.Count > 0)
            {
                stopAttempted = true;
                await stop(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            result = await install();
        }
        catch (Exception ex)
        {
            result = new ProcessRunResult(-1, "", ex is OperationCanceledException
                ? "安装已取消。" : ExceptionMessageFormatter.Format(ex));
        }
        finally
        {
            if (stopAttempted)
            {
                try { recoveryWarning = await restore(result.Succeeded); }
                catch (Exception ex) { recoveryWarning = ExceptionMessageFormatter.Format(ex); }
            }
        }
        if (!string.IsNullOrWhiteSpace(recoveryWarning))
        {
            var detail = "后台服务恢复未完成：\n" + recoveryWarning +
                "\n请检查上述服务状态，必要时在 Windows 服务管理器中恢复；不要重复安装来掩盖此错误。";
            result = result.Succeeded
                ? result with { RecoveryWarning = detail }
                : result with { StandardError = result.ErrorMessage + "\n\n" + detail };
        }
        return result;
    }
}
