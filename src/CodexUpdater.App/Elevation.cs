using System.Diagnostics;
using System.Security.Principal;

namespace CodexUpdater.App;

internal static class Elevation
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RelaunchElevatedForInstall(string packagePath, string architecture)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("Cannot find current executable path.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--install {Quote(packagePath)} --arch {Quote(architecture)}",
            UseShellExecute = true,
            Verb = "runas",
        });
    }

    private static string Quote(string value)
    {
        return '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }
}
