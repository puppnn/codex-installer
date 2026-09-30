using System.Windows;

namespace CodexUpdater.App;

internal static class WindowLayout
{
    public static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        var width = Math.Max(300, area.Width - 24);
        var height = Math.Max(300, area.Height - 24);
        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        window.Width = Math.Min(window.Width, width);
        window.Height = Math.Min(window.Height, height);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
}
