using System.Diagnostics;
using System.IO;

namespace CodexUpdater.App;

internal static class DownloadFolderService
{
    public static void Open(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add(fullPath);
        Process.Start(startInfo)?.Dispose();
    }
}
